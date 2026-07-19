namespace GraphMailer.Service.Infrastructure.Config;

/// <summary>
/// Single source of truth for the <b>array-shaped</b> configuration defaults that a fresh
/// install should start with — the SMTP listeners and the IP whitelist.
///
/// <para>
/// Why these live in code instead of <c>appsettings.json</c>: the service binds
/// <c>Servers</c> and the access-control arrays through <c>IConfiguration</c>, whose array
/// overlay merges <b>by index</b> rather than replacing. Array defaults in
/// <c>appsettings.json</c> would therefore "leak" trailing entries back in whenever the user's
/// <c>graphmailer.json</c> defines a shorter list. To avoid that, <c>appsettings.json</c> keeps
/// these arrays empty and the defaults here are materialised once into <c>graphmailer.json</c>
/// on first run (see the first-run provisioner in <c>Program.cs</c>). Scalar/object defaults do
/// not have this problem and remain centralised in <c>appsettings.json</c>.
/// </para>
///
/// Both the first-run provisioner and the ConfigTool's "no listeners configured" fallback use
/// this type so they never diverge.
/// </summary>
internal static class DefaultConfiguration
{
    /// <summary>Subject (CN) of the auto-generated self-signed SMTP certificate.</summary>
    internal const string SelfSignedSubjectName = "GraphMailer SMTP";

    /// <summary>
    /// Industry-standard SMTP listeners for a fresh install: plaintext submission on 25,
    /// implicit TLS on 465, and STARTTLS on 587. Authentication is optional on the encrypted
    /// connectors and not required on plain 25 (the IP whitelist gates plaintext access).
    /// </summary>
    internal static List<ConfigDocument.ServerEntry> Servers() =>
    [
        new() { Enabled = true, Name = "SMTP (Plain)",            Port = 25,  Mode = "Plain",    AuthMode = "None" },
        new() { Enabled = true, Name = "SMTPS (Implicit TLS)",    Port = 465, Mode = "Tls",      AuthMode = "Optional" },
        new() { Enabled = true, Name = "Submission (STARTTLS)",   Port = 587, Mode = "StartTls", AuthMode = "Optional" },
    ];

    /// <summary>
    /// Default IP whitelist covering the private/internal address space (RFC 1918, loopback,
    /// IPv6 unique-local and link-local). A non-empty whitelist means <b>only</b> these ranges
    /// may send mail — public senders are rejected at MAIL FROM, a sensible default for an
    /// internal relay.
    /// </summary>
    internal static List<string> IpWhitelist() =>
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "127.0.0.0/8",
        "::1/128",
        "fc00::/7",
        "fe80::/10",
    ];

    /// <summary>Friendly comments shown next to each default whitelist entry in the ConfigTool.</summary>
    internal static Dictionary<string, string> IpWhitelistComments() => new()
    {
        ["10.0.0.0/8"]     = "Private network (RFC 1918)",
        ["172.16.0.0/12"]  = "Private network (RFC 1918)",
        ["192.168.0.0/16"] = "Private network (RFC 1918)",
        ["127.0.0.0/8"]    = "IPv4 loopback",
        ["::1/128"]        = "IPv6 loopback",
        ["fc00::/7"]       = "IPv6 unique-local (RFC 4193)",
        ["fe80::/10"]      = "IPv6 link-local",
    };
}
