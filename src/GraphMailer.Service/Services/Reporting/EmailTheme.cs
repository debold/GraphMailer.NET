namespace GraphMailer.Service.Services.Reporting;

/// <summary>
/// Shared visual identity for all HTML emails (operations report, admin notifications,
/// NDRs, backup mail, ConfigTool test mail): GitHub-Primer palette and font stacks.
/// Keep <see cref="HtmlReportRenderer"/> and <see cref="NotificationHtmlRenderer"/> on
/// these constants so every system mail renders as one family.
/// </summary>
internal static class EmailTheme
{
    // Primer palette
    internal const string Border = "#d0d7de", Hair = "#eaeef2", Text = "#1f2328", Muted = "#656d76",
                          Faint = "#8c959f", Accent = "#0969da", PageBg = "#f6f8fa", Dark = "#1f2328";
    internal const string OkFg = "#1a7f37", OkBg = "#dafbe1", OkBorder = "#aceebb";
    internal const string WarnFg = "#9a6700", WarnBg = "#fff8c5", WarnBorder = "#eac54f";
    internal const string DangerFg = "#cf222e", DangerBg = "#ffebe9", DangerBorder = "#ffcecb", DangerDark = "#86181d";
    internal const string InfoFg = "#0969da", InfoBg = "#ddf4ff", InfoBorder = "#a5d6ff";

    internal const string SansFont = "'Segoe UI',Arial,sans-serif";
    internal const string MonoFont = "Consolas,'Courier New',monospace";
}
