namespace GraphMailer.Service.Configuration;

public sealed class UserEntry
{
    /// <summary>
    /// When false, the user is rejected at SMTP AUTH without checking the password.
    /// Defaults to true so configs written before this flag existed keep working.
    /// </summary>
    public bool Enabled { get; init; } = true;

    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Password for this SMTP user. Store as ENC[...] in config\graphmailer.json.
    /// Plaintext is accepted on first setup and should be replaced with an encrypted
    /// value via the dashboard as soon as possible.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// When true, the next successful SMTP AUTH attempt by this user will be used
    /// to capture and persist the password. The flag is cleared automatically after
    /// the first successful capture.
    /// </summary>
    public bool CaptureNextPassword { get; init; }

    public List<string> FromRestrictions { get; init; } = [];
}

public sealed class IpBlockingProtectionOptions
{
    public const string SectionName = "IpBlockingProtection";

    public bool Enabled { get; init; } = true;
    public int FailureThreshold { get; init; } = 10;
    public int TimeframeSeconds { get; init; } = 600;
    public int BlockDurationSeconds { get; init; } = 600;
}
