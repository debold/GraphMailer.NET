namespace GraphMailer.Service.Configuration;

/// <summary>
/// Holds all per-request access-control lists that are bound from the
/// configuration root (not a specific section), because each list is a
/// top-level JSON array in graphmailer.json / appsettings.json.
/// Property names must therefore match the top-level JSON keys exactly.
/// </summary>
public sealed class SmtpAccessOptions
{
    public List<UserEntry> Users { get; set; } = [];
    public List<string> IpWhitelist { get; set; } = [];
    public List<string> IpBlacklist { get; set; } = [];
    public List<string> AllowedSenders { get; set; } = [];
    public List<string> BlockedSenders { get; set; } = [];
    public List<string> AllowedRecipients { get; set; } = [];
    public List<string> BlockedRecipients { get; set; } = [];
}
