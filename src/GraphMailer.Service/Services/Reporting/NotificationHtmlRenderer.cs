using System.Text;
using GraphMailer.Service.Infrastructure;
using static GraphMailer.Service.Services.Reporting.EmailTheme;

namespace GraphMailer.Service.Services.Reporting;

/// <summary>Severity of a system notification email; controls the banner styling.</summary>
internal enum NotificationSeverity { Success, Info, Warning, Critical }

/// <summary>One label/value row in the details table of a notification email.</summary>
internal sealed record NotificationField(string Label, string Value);

/// <summary>
/// Structured content of a system notification email, rendered by
/// <see cref="NotificationHtmlRenderer"/>. All values are treated as untrusted text
/// and HTML-encoded at render time.
/// </summary>
internal sealed record NotificationEmail
{
    public required NotificationSeverity Severity { get; init; }

    /// <summary>Banner headline, e.g. "Certificate has expired".</summary>
    public required string Title { get; init; }

    /// <summary>Optional paragraph below the banner explaining the event.</summary>
    public string? Intro { get; init; }

    /// <summary>Label/value details table (empty = omitted).</summary>
    public IReadOnlyList<NotificationField> Fields { get; init; } = [];

    /// <summary>Optional heading for <see cref="Items"/>.</summary>
    public string? ItemsTitle { get; init; }

    /// <summary>Monospace line items (batched failures, affected config paths, …).</summary>
    public IReadOnlyList<string> Items { get; init; } = [];

    /// <summary>Optional guidance paragraph (likely cause, how to fix).</summary>
    public string? Note { get; init; }

    public string? LinkUrl { get; init; }
    public string? LinkLabel { get; init; }

    /// <summary>Right-hand header label ("System Notification", "Non-Delivery Report", …).</summary>
    public string Kicker { get; init; } = "System Notification";

    /// <summary>Overrides the default footer sentence about the ConfigTool Notifications page.</summary>
    public string? FooterNote { get; init; }
}

/// <summary>
/// Renders a <see cref="NotificationEmail"/> into the Outlook-safe HTML email shared by all
/// GraphMailer system mails (same GitHub-Primer shell as <see cref="HtmlReportRenderer"/>:
/// table layout, inline styles, dark header, severity banner). All dynamic text is HTML-encoded.
/// </summary>
internal static class NotificationHtmlRenderer
{
    public static string Render(NotificationEmail n)
    {
        var sb = new StringBuilder(8_192);

        sb.Append("""
            <!DOCTYPE html>
            <html lang="en" xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <meta http-equiv="X-UA-Compatible" content="IE=edge">
            <!--[if mso]><style type="text/css">table,td,th{border-collapse:collapse;mso-table-lspace:0pt;mso-table-rspace:0pt;}body,table,td,th{font-family:'Segoe UI',Arial,sans-serif!important;}.mono{font-family:Consolas,'Courier New',monospace!important;}</style><![endif]-->
            <style type="text/css">body{margin:0;padding:0;background-color:#f6f8fa;}table{border-collapse:collapse;}@media only screen and (max-width:640px){.container{width:100%!important;}}</style>
            </head>
            <body style="margin:0;padding:0;background-color:#f6f8fa;">
            """);

        // Preheader
        sb.Append($"""<div style="display:none;max-height:0;overflow:hidden;mso-hide:all;font-size:1px;line-height:1px;color:#f6f8fa;">{Enc(n.Intro ?? n.Title)}</div>""");

        sb.Append($"""
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="#f6f8fa" style="background-color:#f6f8fa;"><tr><td align="center" style="padding:24px 12px;">
            <!--[if mso]><table role="presentation" width="640" cellpadding="0" cellspacing="0" border="0"><tr><td><![endif]-->
            <table role="presentation" class="container" width="640" cellpadding="0" cellspacing="0" border="0" style="width:640px;max-width:640px;background-color:#ffffff;border:1px solid {Border};">
            """);

        AppendHeader(sb, n);
        AppendMetaStrip(sb);
        AppendBanner(sb, n);
        AppendIntro(sb, n);
        AppendFields(sb, n);
        AppendItems(sb, n);
        AppendNote(sb, n);
        AppendLink(sb, n);
        AppendFooter(sb, n);

        sb.Append("""
            </table>
            <!--[if mso]></td></tr></table><![endif]-->
            </td></tr></table>
            </body></html>
            """);

        return sb.ToString();
    }

    // ── Sections ─────────────────────────────────────────────────────────────

    private static void AppendHeader(StringBuilder sb, NotificationEmail n)
    {
        sb.Append($"""
            <tr><td bgcolor="{Dark}" style="background-color:{Dark};padding:20px 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr>
            <td align="left" valign="middle" style="font-family:{SansFont};">
            <span style="font-family:{MonoFont};font-size:18px;font-weight:700;color:#ffffff;letter-spacing:-0.2px;">Graph<span style="color:#6cb6ff;">Mailer</span></span>
            <div style="font-family:{SansFont};font-size:12px;color:#9da7b3;padding-top:4px;">SMTP&nbsp;Relay · Microsoft&nbsp;365&nbsp;Graph</div>
            </td>
            <td align="right" valign="middle" style="font-family:{SansFont};font-size:12px;color:#9da7b3;line-height:18px;">
            <span style="color:#ffffff;font-size:13px;font-weight:600;">{Enc(n.Kicker)}</span><br>
            <span class="mono" style="font-family:{MonoFont};color:#9da7b3;">{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC</span>
            </td></tr></table></td></tr>
            """);
    }

    private static void AppendMetaStrip(StringBuilder sb)
    {
        sb.Append($"""
            <tr><td bgcolor="{PageBg}" style="background-color:{PageBg};border-bottom:1px solid {Border};padding:10px 28px;font-family:{MonoFont};font-size:12px;color:{Muted};">
            host&nbsp;<span style="color:{Text};">{Enc(Environment.MachineName)}</span>&nbsp;&nbsp;·&nbsp;&nbsp;service&nbsp;<span style="color:{Text};">v{Enc(BuildInfo.FileVersion)}</span>
            </td></tr>
            """);
    }

    private static void AppendBanner(StringBuilder sb, NotificationEmail n)
    {
        var (bg, border, bar, fg) = n.Severity switch
        {
            NotificationSeverity.Success => (OkBg, OkBorder, OkFg, OkFg),
            NotificationSeverity.Warning => (WarnBg, WarnBorder, WarnFg, WarnFg),
            NotificationSeverity.Critical => (DangerBg, DangerBorder, DangerFg, DangerFg),
            _ => (InfoBg, InfoBorder, InfoFg, InfoFg),
        };

        sb.Append($"""
            <tr><td style="padding:20px 28px 4px 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="{bg}" style="background-color:{bg};border:1px solid {border};"><tr>
            <td valign="top" width="6" bgcolor="{bar}" style="background-color:{bar};width:6px;font-size:0;line-height:0;">&nbsp;</td>
            <td style="padding:12px 16px;font-family:{SansFont};">
            <div style="font-size:14px;font-weight:600;color:{fg};">{Enc(n.Title)}</div>
            </td></tr></table></td></tr>
            """);
    }

    private static void AppendIntro(StringBuilder sb, NotificationEmail n)
    {
        if (string.IsNullOrEmpty(n.Intro))
            return;
        sb.Append($"""
            <tr><td style="padding:16px 28px 0 28px;font-family:{SansFont};font-size:13px;color:{Text};line-height:20px;">{Enc(n.Intro)}</td></tr>
            """);
    }

    private static void AppendFields(StringBuilder sb, NotificationEmail n)
    {
        if (n.Fields.Count == 0)
            return;

        sb.Append($"""<tr><td style="padding:16px 28px 0 28px;"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid {Border};">""");
        for (int i = 0; i < n.Fields.Count; i++)
        {
            var f = n.Fields[i];
            var bb = i == n.Fields.Count - 1 ? "" : $"border-bottom:1px solid {Hair};";
            sb.Append($"""
                <tr>
                <td width="160" valign="top" bgcolor="{PageBg}" style="{bb}background-color:{PageBg};border-right:1px solid {Hair};padding:9px 12px;font-family:{SansFont};font-size:11px;font-weight:700;letter-spacing:0.4px;text-transform:uppercase;color:{Muted};">{Enc(f.Label)}</td>
                <td valign="top" class="mono" style="{bb}padding:9px 12px;font-family:{MonoFont};font-size:12px;color:{Text};word-break:break-word;">{Enc(f.Value)}</td>
                </tr>
                """);
        }
        sb.Append("</table></td></tr>");
    }

    private static void AppendItems(StringBuilder sb, NotificationEmail n)
    {
        if (n.Items.Count == 0)
            return;

        if (!string.IsNullOrEmpty(n.ItemsTitle))
        {
            sb.Append($"""
                <tr><td style="padding:20px 28px 8px 28px;font-family:{SansFont};">
                <div style="font-size:11px;font-weight:700;letter-spacing:0.6px;text-transform:uppercase;color:{Muted};border-bottom:1px solid {Hair};padding-bottom:6px;">{Enc(n.ItemsTitle)}</div>
                </td></tr>
                """);
        }

        sb.Append($"""<tr><td style="padding:{(string.IsNullOrEmpty(n.ItemsTitle) ? "16px" : "0")} 28px 0 28px;"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid {Border};">""");
        for (int i = 0; i < n.Items.Count; i++)
        {
            var bb = i == n.Items.Count - 1 ? "" : $"border-bottom:1px solid {Hair};";
            sb.Append($"""<tr><td class="mono" style="{bb}padding:8px 12px;font-family:{MonoFont};font-size:12px;color:{Text};word-break:break-word;">{Enc(n.Items[i])}</td></tr>""");
        }
        sb.Append("</table></td></tr>");
    }

    private static void AppendNote(StringBuilder sb, NotificationEmail n)
    {
        if (string.IsNullOrEmpty(n.Note))
            return;
        sb.Append($"""
            <tr><td style="padding:16px 28px 0 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="{PageBg}" style="background-color:{PageBg};border:1px solid {Border};"><tr>
            <td style="padding:10px 14px;font-family:{SansFont};font-size:12px;color:{Muted};line-height:18px;">{Enc(n.Note)}</td>
            </tr></table></td></tr>
            """);
    }

    private static void AppendLink(StringBuilder sb, NotificationEmail n)
    {
        if (string.IsNullOrEmpty(n.LinkUrl))
            return;
        // Bulletproof button: a filled table cell, not CSS padding on the <a>.
        sb.Append($"""
            <tr><td style="padding:20px 28px 0 28px;">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0"><tr>
            <td bgcolor="{Accent}" style="background-color:{Accent};">
            <a href="{Enc(n.LinkUrl)}" target="_blank" style="display:inline-block;padding:9px 18px;font-family:{SansFont};font-size:13px;font-weight:600;color:#ffffff;text-decoration:none;">{Enc(n.LinkLabel ?? n.LinkUrl)}</a>
            </td></tr></table></td></tr>
            """);
    }

    private static void AppendFooter(StringBuilder sb, NotificationEmail n)
    {
        var note = n.FooterNote
                   ?? "Notification types and recipients are managed in the ConfigTool → Notifications page. This is an unmonitored mailbox — do not reply.";
        sb.Append($"""
            <tr><td style="padding:24px 0 0 0;font-size:0;line-height:0;">&nbsp;</td></tr>
            <tr><td bgcolor="{PageBg}" style="background-color:{PageBg};border-top:1px solid {Border};padding:16px 28px;font-family:{SansFont};font-size:11px;color:{Faint};line-height:16px;">
            Automatically generated by <span class="mono" style="font-family:{MonoFont};color:{Muted};">GraphMailer v{Enc(BuildInfo.FileVersion)}</span> on <span class="mono" style="font-family:{MonoFont};color:{Muted};">{Enc(Environment.MachineName)}</span>.<br>
            {Enc(note)}
            </td></tr>
            """);
    }

    private static string Enc(string? s) => HtmlReportRenderer.Enc(s);
}
