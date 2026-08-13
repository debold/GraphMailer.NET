using GraphMailer.Service.Infrastructure.Security.Amsi;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// Stands in for a machine with no AMSI provider. The default for every host, so that no test
/// suite ever hands mail to the build machine's real antimalware product — that would make
/// results depend on installed signatures and would raise genuine antivirus events.
/// </summary>
internal sealed class UnavailableScanner : IMailContentScanner
{
    internal static readonly IMailContentScanner Instance = new UnavailableScanner();

    public bool IsAvailable => false;
    public IReadOnlyList<AmsiProvider> Providers => [];

    public Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> eml, string messageId, CancellationToken ct = default)
        => Task.FromResult(ScanResult.Unavailable());
}

/// <summary>
/// Returns a fixed verdict for every message, so a test can drive the SMTP path through a
/// detection without needing content that a real scanner would actually flag.
/// </summary>
internal sealed class ScriptedScanner(ScanResult result) : IMailContentScanner
{
    public bool IsAvailable => true;
    public IReadOnlyList<AmsiProvider> Providers { get; } = [new("{test}", "Scripted Test Provider", "test.dll")];

    public int ScanCount { get; private set; }

    public Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> eml, string messageId, CancellationToken ct = default)
    {
        ScanCount++;
        return Task.FromResult(result);
    }

    internal static ScriptedScanner DetectsAttachment(string name = "invoice.docm", string hash = "abc123")
        => new(new ScanResult(ScanOutcome.Malware, name, hash, 4096, 32768));

    internal static ScriptedScanner Clean() => new(ScanResult.Clean());

    internal static ScriptedScanner Fails() => new(ScanResult.Failed("provider unavailable"));
}
