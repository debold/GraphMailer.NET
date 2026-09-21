using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using System.Threading;

namespace GraphMailer.ConfigTool.Services;

/// <summary>
/// What the ConfigTool could determine about the app registration's Graph permissions.
/// <see cref="Unknown"/> covers every reason the question could not be answered — not
/// configured, no network, bad credentials — because none of them is evidence of a gap and
/// none should be shown as one.
/// </summary>
internal enum GraphPermissionState { Unknown, Complete, Incomplete }

/// <param name="Missing">The required roles the token does not carry; empty unless
/// <see cref="GraphPermissionState.Incomplete"/>.</param>
/// <param name="Reason">Why the state is <see cref="GraphPermissionState.Unknown"/>.</param>
internal sealed record GraphPermissionCheckResult(
    GraphPermissionState State,
    IReadOnlyList<GraphAppRole> Missing,
    string? Reason);

/// <summary>
/// Reads the Graph application permissions actually granted to the app registration, so the
/// ConfigTool can show the same gap the service mails about instead of a green box that only
/// proves a tenant id and a client id are filled in.
///
/// It takes the same route as the service's own monitor: acquire an app-only token and read its
/// <c>roles</c> claim. That needs no admin sign-in and no directory read permission — the token
/// states its own grants — so it can run unattended whenever the Graph API page is opened.
/// </summary>
internal static class GraphPermissionCheckService
{
    /// <summary>
    /// Checks the credentials as they stand in the ConfigTool against what
    /// <paramref name="senderValidation"/> makes necessary. Never throws: every failure becomes
    /// <see cref="GraphPermissionState.Unknown"/> with a reason, because a permission check is a
    /// diagnostic and must not be able to break the page that hosts it.
    /// </summary>
    internal static async Task<GraphPermissionCheckResult> CheckAsync(
        string? tenantId,
        string? clientId,
        string? clientSecret,
        string? certThumbprint,
        SenderValidationOptions senderValidation,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
            return new GraphPermissionCheckResult(
                GraphPermissionState.Unknown, [], "Tenant ID and Client ID are not configured");

        if (string.IsNullOrWhiteSpace(clientSecret) && string.IsNullOrWhiteSpace(certThumbprint))
            return new GraphPermissionCheckResult(
                GraphPermissionState.Unknown, [], "No client secret or certificate configured");

        try
        {
            var token = await GraphApiTestService.AcquireTokenAsync(
                tenantId, clientId, clientSecret, certThumbprint, ct);

            var granted = GraphConnectivityProbe.ParseRoles(token.AccessToken);
            var missing = GraphPermissions.Missing(granted, senderValidation);

            return missing.Count == 0
                ? new GraphPermissionCheckResult(GraphPermissionState.Complete, [], null)
                : new GraphPermissionCheckResult(GraphPermissionState.Incomplete, missing, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the page closed or a newer check superseded this one
        }
        catch (Exception ex)
        {
            return new GraphPermissionCheckResult(GraphPermissionState.Unknown, [], ex.Message);
        }
    }
}
