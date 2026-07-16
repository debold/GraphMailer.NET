using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GraphMailer.Tests.Live;

/// <summary>
/// Settings for tests against a real Microsoft 365 test tenant.
///
/// Values are read from (later sources override earlier ones):
///   1. .NET user secrets (id "GraphMailer.Tests.Live") — recommended on dev machines;
///      populate via tools\set-live-test-secrets.ps1
///   2. livesettings.local.json next to the test project (gitignored)
///   3. Environment variables, prefix GRAPHMAILER_ (e.g. GRAPHMAILER_LiveTests__TenantId) — for CI
///
/// No source contains values → all live tests are skipped (see <see cref="LiveFactAttribute"/>).
/// </summary>
public sealed class LiveTestSettings
{
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? CertificateThumbprint { get; init; }
    public string? CertificateSubjectName { get; init; }

    /// <summary>Sender mailbox in the test tenant (UPN or primary SMTP).</summary>
    public string? SenderAddress { get; init; }

    /// <summary>Recipient mailbox in the test tenant — keeps test mail inside the tenant.</summary>
    public string? RecipientAddress { get; init; }

    /// <summary>Optional: a secondary proxyAddress of the sender, for the alias test.</summary>
    public string? SenderAlias { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        (!string.IsNullOrWhiteSpace(ClientSecret) ||
         !string.IsNullOrWhiteSpace(CertificateThumbprint) ||
         !string.IsNullOrWhiteSpace(CertificateSubjectName)) &&
        !string.IsNullOrWhiteSpace(SenderAddress) &&
        !string.IsNullOrWhiteSpace(RecipientAddress);

    public GraphApiOptions ToGraphApiOptions() => new()
    {
        TenantId = TenantId,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        ClientCertificateThumbprint = CertificateThumbprint,
        ClientCertificateSubjectName = CertificateSubjectName,
    };

    private static readonly Lazy<LiveTestSettings> _current = new(Load);

    public static LiveTestSettings Current => _current.Value;

    private static LiveTestSettings Load()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<LiveTestSettings>(optional: true)
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "livesettings.local.json"), optional: true)
            .AddEnvironmentVariables(prefix: "GRAPHMAILER_")
            .Build();

        var settings = new LiveTestSettings();
        config.GetSection("LiveTests").Bind(settings);
        return settings;
    }
}

/// <summary>
/// A [Fact] that runs only when the live test tenant is configured.
/// Without configuration the test is reported as skipped — normal
/// `dotnet test` runs stay green and never touch the network.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    /// <summary>Set true for tests that additionally need LiveTests:SenderAlias.</summary>
    public bool RequireSenderAlias
    {
        get => _requireSenderAlias;
        set
        {
            _requireSenderAlias = value;
            if (value && string.IsNullOrWhiteSpace(LiveTestSettings.Current.SenderAlias))
                Skip = "LiveTests:SenderAlias not configured";
        }
    }
    private bool _requireSenderAlias;

    public LiveFactAttribute()
    {
        if (!LiveTestSettings.Current.IsConfigured)
            Skip = "Live test tenant not configured (see tools\\set-live-test-secrets.ps1)";
    }
}

/// <summary>Minimal IOptionsMonitor for handing fixed options to service classes.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
