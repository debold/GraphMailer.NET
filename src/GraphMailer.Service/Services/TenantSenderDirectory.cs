using System.Collections.Concurrent;
using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models.ODataErrors;

namespace GraphMailer.Service.Services;

internal enum SenderLookupResult
{
    /// <summary>The address belongs to a tenant user (incl. aliases / shared mailboxes).</summary>
    Valid,
    /// <summary>Graph positively confirmed there is no such sender in the tenant.</summary>
    Unknown,
    /// <summary>Validation impossible right now (Graph unreachable, permission missing, timeout).</summary>
    Indeterminate,
}

/// <summary>Outcome of a full directory sync, surfaced in the ConfigTool status display.</summary>
internal sealed record SenderDirectoryRefreshResult(bool Success, int UserCount, int AddressCount, string? Error);

/// <summary>
/// Cached view of the tenant's sender addresses, used to validate SMTP MAIL FROM
/// before a message is queued.
/// </summary>
internal interface ITenantSenderDirectory
{
    Task<SenderLookupResult> ValidateAsync(string address, CancellationToken ct = default);

    /// <summary>
    /// Resolves an SMTP address to the Graph object id of the owning user, so aliases
    /// (secondary proxyAddresses) can be used as user key in /users/{key}/sendMail.
    /// Returns false when the feature is disabled or the address is not cached.
    /// </summary>
    bool TryResolveGraphUserKey(string address, out string graphUserKey);

    /// <summary>Full directory sync; replaces the positive cache atomically. Never throws.</summary>
    Task<SenderDirectoryRefreshResult> RefreshAsync(CancellationToken ct = default);
}

/// <summary>
/// Caching strategy (pattern follows IpBlockingService — ConcurrentDictionary, lazy expiry):
///   - Positive cache: address → TenantUser, rebuilt by full sync (atomic reference swap,
///     lock-free reads) and extended by on-demand lookup hits.
///   - Negative cache: address → expiry, so repeated unknown senders don't hammer Graph.
///   - On-demand lookups are bounded (SemaphoreSlim) and time-limited so MAIL FROM
///     never hangs on a slow Graph call.
/// Fail-open: any Graph failure yields Indeterminate; the caller decides (FailClosed).
/// </summary>
internal sealed class TenantSenderDirectory : ITenantSenderDirectory
{
    private readonly IGraphDirectoryGateway _gateway;
    private readonly IOptionsMonitor<SenderValidationOptions> _options;
    private readonly IOptionsMonitor<GraphApiOptions> _graphOptions;
    private readonly IAdminNotificationService _notify;
    private readonly ILogger<TenantSenderDirectory> _logger;

    private volatile ConcurrentDictionary<string, TenantUser> _byAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _negative =
        new(StringComparer.OrdinalIgnoreCase);

    // Bounds concurrent on-demand Graph lookups triggered from MAIL FROM
    private readonly SemaphoreSlim _lookupSemaphore = new(2, 2);

    // 0 = no outage notification pending; 1 = already notified for the current outage
    private int _outageNotified;

    public TenantSenderDirectory(
        IGraphDirectoryGateway gateway,
        IOptionsMonitor<SenderValidationOptions> options,
        IOptionsMonitor<GraphApiOptions> graphOptions,
        IAdminNotificationService notify,
        ILogger<TenantSenderDirectory> logger)
    {
        _gateway = gateway;
        _options = options;
        _graphOptions = graphOptions;
        _notify = notify;
        _logger = logger;
    }

    public async Task<SenderLookupResult> ValidateAsync(string address, CancellationToken ct = default)
    {
        // Null reverse path (MAIL FROM:<>) — legitimate for NDRs, never validated
        if (address == "@")
            return SenderLookupResult.Valid;

        // Positive cache (shared mailboxes have AccountEnabled=false but are valid senders)
        if (_byAddress.ContainsKey(address))
            return SenderLookupResult.Valid;

        // Negative cache with lazy expiry
        if (_negative.TryGetValue(address, out var expiresAt))
        {
            if (DateTime.UtcNow < expiresAt)
                return SenderLookupResult.Unknown;
            _negative.TryRemove(address, out _);
        }

        if (!_graphOptions.CurrentValue.IsConfigured)
            return SenderLookupResult.Indeterminate;

        return await LookupOnDemandAsync(address, ct);
    }

    public bool TryResolveGraphUserKey(string address, out string graphUserKey)
    {
        graphUserKey = string.Empty;
        if (!_options.CurrentValue.Enabled) return false;
        if (!_byAddress.TryGetValue(address, out var user)) return false;
        graphUserKey = user.Id;
        return true;
    }

    public async Task<SenderDirectoryRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var users = await _gateway.GetAllUsersAsync(ct);

            var map = new ConcurrentDictionary<string, TenantUser>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in users)
                foreach (var addr in user.SmtpAddresses)
                    map.TryAdd(addr, user);

            _byAddress = map;          // atomic swap — readers never see a partial cache
            _negative.Clear();         // fresh sync supersedes all negative results
            OnGraphSuccess();

            _logger.LogInformation(
                "[SenderValidation] Directory sync complete: {Users} user(s), {Addresses} sender address(es)",
                users.Count, map.Count);

            return new SenderDirectoryRefreshResult(true, users.Count, map.Count, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown — not an outage
            return new SenderDirectoryRefreshResult(false, 0, 0, "canceled");
        }
        catch (Exception ex)
        {
            await OnGraphFailureAsync("directory sync", ex, ct);
            return new SenderDirectoryRefreshResult(false, 0, 0, ex.Message);
        }
    }

    private async Task<SenderLookupResult> LookupOnDemandAsync(string address, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, opts.LookupTimeoutSeconds)));

        try
        {
            await _lookupSemaphore.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "[SenderValidation] Lookup for {Address} skipped — too many concurrent lookups", address);
            return SenderLookupResult.Indeterminate;
        }

        try
        {
            var user = await _gateway.FindBySmtpAddressAsync(address, cts.Token);
            OnGraphSuccess();

            if (user is null)
            {
                _negative[address] = DateTime.UtcNow.AddSeconds(Math.Max(1, opts.NegativeCacheSeconds));
                _logger.LogInformation(
                    "[SenderValidation] Sender {Address} not found in tenant (negative-cached for {Ttl}s)",
                    address, opts.NegativeCacheSeconds);
                return SenderLookupResult.Unknown;
            }

            foreach (var addr in user.SmtpAddresses)
                _byAddress.TryAdd(addr, user);

            _logger.LogDebug("[SenderValidation] Sender {Address} resolved to user {Id}", address, user.Id);
            return SenderLookupResult.Valid;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[SenderValidation] Lookup for {Address} timed out after {Timeout}s",
                address, opts.LookupTimeoutSeconds);
            return SenderLookupResult.Indeterminate;
        }
        catch (Exception ex)
        {
            await OnGraphFailureAsync($"lookup for {address}", ex, ct);
            return SenderLookupResult.Indeterminate;
        }
        finally
        {
            _lookupSemaphore.Release();
        }
    }

    private void OnGraphSuccess() => Interlocked.Exchange(ref _outageNotified, 0);

    /// <summary>Logs the failure (with a permission hint on 403) and notifies once per outage.</summary>
    private async Task OnGraphFailureAsync(string operation, Exception ex, CancellationToken ct)
    {
        var detail = ex is ODataError { ResponseStatusCode: 403 }
            ? "access denied — grant the User.Read.All application permission to the Entra app registration"
            : ex.Message;

        _logger.LogWarning(
            "[SenderValidation] Graph {Operation} failed ({Detail}) — senders are accepted unvalidated (fail-open)",
            operation, detail);

        if (Interlocked.Exchange(ref _outageNotified, 1) == 0)
        {
            try
            {
                await _notify.NotifyGraphApiErrorAsync($"Sender validation {operation} failed: {detail}", ct);
            }
            catch (Exception notifyEx)
            {
                _logger.LogDebug(notifyEx, "[SenderValidation] Admin notification failed");
            }
        }
    }
}
