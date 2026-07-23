using System.Globalization;
using System.Net;
using System.Text;
using GraphMailer.Service.Services.Advisor;

namespace GraphMailer.Service.Services.Reporting;

/// <summary>
/// Renders a <see cref="ReportData"/> snapshot into the Outlook-safe HTML report email
/// (GitHub-Primer styling: table layout, inline styles, square chips). The daily-volume chart
/// is a server-rendered PNG (<see cref="DailyChartImage"/>) embedded as a CID inline image —
/// Outlook Classic and new Outlook both strip inline SVG. All dynamic text is HTML-encoded.
/// </summary>
internal static class HtmlReportRenderer
{
    // Primer palette — single source in EmailTheme, aliased to keep the template code terse.
    private const string Border = EmailTheme.Border, Hair = EmailTheme.Hair, Text = EmailTheme.Text,
                         Muted = EmailTheme.Muted, Faint = EmailTheme.Faint, Accent = EmailTheme.Accent,
                         PageBg = EmailTheme.PageBg, Dark = EmailTheme.Dark;
    private const string OkFg = EmailTheme.OkFg, OkBg = EmailTheme.OkBg, OkBorder = EmailTheme.OkBorder;
    private const string WarnFg = EmailTheme.WarnFg, WarnBg = EmailTheme.WarnBg, WarnBorder = EmailTheme.WarnBorder;
    private const string DangerFg = EmailTheme.DangerFg, DangerBg = EmailTheme.DangerBg,
                         DangerBorder = EmailTheme.DangerBorder, DangerDark = EmailTheme.DangerDark;

    private const string SansFont = EmailTheme.SansFont;
    private const string MonoFont = EmailTheme.MonoFont;

    /// <summary>Content-ID under which the daily-volume PNG is attached and referenced (<c>cid:…</c>).</summary>
    internal const string ChartContentId = "daily-volume@graphmailer";

    /// <summary>Rendered report: the HTML body plus the daily-volume chart PNG to embed as a CID inline image (null when there is no chart data).</summary>
    internal sealed record ReportEmail(string Html, byte[]? ChartPng, string ChartContentId);

    public static ReportEmail Render(ReportData d)
    {
        byte[]? chartPng = d.Daily.Count > 0 ? DailyChartImage.Render(d.Daily, Accent, DangerFg) : null;

        var sb = new StringBuilder(16_384);

        sb.Append("""
            <!DOCTYPE html>
            <html lang="en" xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <meta http-equiv="X-UA-Compatible" content="IE=edge">
            <!--[if mso]><style type="text/css">table,td,th{border-collapse:collapse;mso-table-lspace:0pt;mso-table-rspace:0pt;}body,table,td,th{font-family:'Segoe UI',Arial,sans-serif!important;}.mono{font-family:Consolas,'Courier New',monospace!important;}</style><![endif]-->
            <style type="text/css">body{margin:0;padding:0;background-color:#f6f8fa;}table{border-collapse:collapse;}@media only screen and (max-width:640px){.container{width:100%!important;}.kpi-cell{display:block!important;width:100%!important;box-sizing:border-box;}}</style>
            </head>
            <body style="margin:0;padding:0;background-color:#f6f8fa;">
            """);

        // Preheader
        sb.Append($"""<div style="display:none;max-height:0;overflow:hidden;mso-hide:all;font-size:1px;line-height:1px;color:#f6f8fa;">{Enc(Preheader(d))}</div>""");

        sb.Append($"""
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="#f6f8fa" style="background-color:#f6f8fa;"><tr><td align="center" style="padding:24px 12px;">
            <!--[if mso]><table role="presentation" width="640" cellpadding="0" cellspacing="0" border="0"><tr><td><![endif]-->
            <table role="presentation" class="container" width="640" cellpadding="0" cellspacing="0" border="0" style="width:640px;max-width:640px;background-color:#ffffff;border:1px solid {Border};">
            """);

        AppendHeader(sb, d);
        AppendMetaStrip(sb, d);
        AppendBanner(sb, d);
        AppendQueueKpis(sb, d);
        AppendFailedQueue(sb, d);
        AppendHealth(sb, d);
        AppendStatistics(sb, d);
        AppendChart(sb, d);
        AppendTopTable(sb, "Top Senders", "by sender address", d.TopSenders, Accent);
        AppendTopTable(sb, "Top Sending Hosts", "by SMTP client (IP)", d.TopHosts, Dark);
        AppendSystem(sb, d);
        AppendRecommendations(sb, d);
        AppendFooter(sb, d);

        sb.Append("""
            </table>
            <!--[if mso]></td></tr></table><![endif]-->
            </td></tr></table>
            </body></html>
            """);

        return new ReportEmail(sb.ToString(), chartPng, ChartContentId);
    }

    // ── Sections ─────────────────────────────────────────────────────────────

    private static void AppendHeader(StringBuilder sb, ReportData d)
    {
        sb.Append($"""
            <tr><td bgcolor="{Dark}" style="background-color:{Dark};padding:20px 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr>
            <td align="left" valign="middle" style="font-family:{SansFont};">
            <span style="font-family:{MonoFont};font-size:18px;font-weight:700;color:#ffffff;letter-spacing:-0.2px;">Graph<span style="color:#6cb6ff;">Mailer</span></span>
            <div style="font-family:{SansFont};font-size:12px;color:#9da7b3;padding-top:4px;">SMTP&nbsp;Relay · Microsoft&nbsp;365&nbsp;Graph</div>
            </td>
            <td align="right" valign="middle" style="font-family:{SansFont};font-size:12px;color:#9da7b3;line-height:18px;">
            <span style="color:#ffffff;font-size:13px;font-weight:600;">{Enc(d.Title)}</span><br>
            <span class="mono" style="font-family:{MonoFont};color:#9da7b3;">{d.PeriodStart:yyyy-MM-dd} → {d.PeriodEnd:yyyy-MM-dd} UTC</span>
            </td></tr></table></td></tr>
            """);
    }

    private static void AppendMetaStrip(StringBuilder sb, ReportData d)
    {
        sb.Append($"""
            <tr><td bgcolor="{PageBg}" style="background-color:{PageBg};border-bottom:1px solid {Border};padding:10px 28px;font-family:{MonoFont};font-size:12px;color:{Muted};">
            host&nbsp;<span style="color:{Text};">{Enc(d.Host)}</span>&nbsp;&nbsp;·&nbsp;&nbsp;service&nbsp;<span style="color:{Text};">v{Enc(d.Version)}</span>&nbsp;&nbsp;·&nbsp;&nbsp;generated&nbsp;<span style="color:{Text};">{d.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC</span>
            </td></tr>
            """);
    }

    private static void AppendBanner(StringBuilder sb, ReportData d)
    {
        string bg, border, bar, fg, sub;
        string title;
        if (d.ErrorCount > 0 || d.FailedQueueCount > 0)
        {
            bg = DangerBg; border = DangerBorder; bar = DangerFg; fg = DangerFg; sub = DangerDark;
            var parts = new List<string>();
            if (d.FailedQueueCount > 0) parts.Add($"{d.FailedQueueCount} message(s) in failed queue");
            if (d.ErrorCount > 0) parts.Add($"{d.ErrorCount} error(s)");
            if (d.WarningCount > 0) parts.Add($"{d.WarningCount} warning(s)");
            title = "Attention — " + string.Join(", ", parts);
        }
        else if (d.WarningCount > 0)
        {
            bg = WarnBg; border = WarnBorder; bar = WarnFg; fg = WarnFg; sub = WarnFg;
            title = $"Operational — {d.WarningCount} warning(s), 0 errors";
        }
        else
        {
            bg = OkBg; border = OkBorder; bar = OkFg; fg = OkFg; sub = OkFg;
            title = "All systems operational";
        }

        var detail = BannerDetail(d);
        sb.Append($"""
            <tr><td style="padding:20px 28px 4px 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="{bg}" style="background-color:{bg};border:1px solid {border};"><tr>
            <td valign="top" width="6" bgcolor="{bar}" style="background-color:{bar};width:6px;font-size:0;line-height:0;">&nbsp;</td>
            <td style="padding:12px 16px;font-family:{SansFont};">
            <div style="font-size:14px;font-weight:600;color:{fg};">{Enc(title)}</div>
            """);
        if (detail.Length > 0)
            sb.Append($"""<div style="font-size:13px;color:{sub};padding-top:2px;">{detail}</div>""");
        sb.Append("</td></tr></table></td></tr>");
    }

    private static void AppendQueueKpis(StringBuilder sb, ReportData d)
    {
        SectionTitle(sb, "Mail Queue &amp; Backlog", null, "16px 28px 0 28px");
        var rate = d.SuccessRatePercent.HasValue ? $"{d.SuccessRatePercent.Value:F1}% success" : "no data";

        sb.Append("""<tr><td style="padding:12px 28px 0 28px;"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr>""");

        // Failed queue — danger styling, drawn first
        var failBg = d.FailedQueueCount > 0 ? DangerBg : "#ffffff";
        var failBorder = d.FailedQueueCount > 0 ? DangerBorder : Border;
        var failFg = d.FailedQueueCount > 0 ? DangerFg : Text;
        sb.Append($"""
            <td class="kpi-cell" width="33%" valign="top" style="padding:0 6px 0 0;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="{failBg}" style="background-color:{failBg};border:1px solid {failBorder};"><tr><td style="padding:12px 14px;font-family:{SansFont};">
            <div style="font-size:11px;color:{failFg};text-transform:uppercase;letter-spacing:0.4px;font-weight:600;">Failed queue</div>
            <div class="mono" style="font-family:{MonoFont};font-size:28px;font-weight:700;color:{failFg};padding-top:2px;">{d.FailedQueueCount}</div>
            <div style="font-size:12px;color:{(d.FailedQueueCount > 0 ? DangerDark : Muted)};padding-top:2px;">needs manual review</div>
            </td></tr></table></td>
            """);

        KpiCard(sb, "Queued now", d.QueuedNow.ToString(CultureInfo.InvariantCulture), "awaiting delivery", Text, "0 6px 0 6px");
        KpiCard(sb, $"Delivered ({DaysLabel(d)})", FormatCount(d.Delivered), rate, OkFg, "0 0 0 6px");

        sb.Append("</tr></table></td></tr>");
    }

    private static void AppendFailedQueue(StringBuilder sb, ReportData d)
    {
        if (d.FailedQueueCount == 0)
            return;

        sb.Append($"""
            <tr><td style="padding:20px 28px 8px 28px;font-family:{SansFont};">
            <div style="font-size:11px;font-weight:700;letter-spacing:0.6px;text-transform:uppercase;color:{DangerFg};border-bottom:1px solid {DangerBorder};padding-bottom:6px;">Failed Queue — Action Required<span style="font-weight:400;text-transform:none;letter-spacing:0;color:{Faint};"> &nbsp;— {d.FailedQueueCount} message(s)</span></div>
            </td></tr>
            <tr><td style="padding:0 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid {DangerBorder};">
            <tr bgcolor="#fff5f5">
            <th align="left" width="104" style="background-color:#fff5f5;border-bottom:1px solid {DangerBorder};padding:8px 12px;font-family:{SansFont};font-size:11px;font-weight:700;letter-spacing:0.4px;text-transform:uppercase;color:{DangerDark};">Failed at</th>
            <th align="left" style="background-color:#fff5f5;border-bottom:1px solid {DangerBorder};padding:8px 12px;font-family:{SansFont};font-size:11px;font-weight:700;letter-spacing:0.4px;text-transform:uppercase;color:{DangerDark};">From → To</th>
            <th align="left" style="background-color:#fff5f5;border-bottom:1px solid {DangerBorder};padding:8px 12px;font-family:{SansFont};font-size:11px;font-weight:700;letter-spacing:0.4px;text-transform:uppercase;color:{DangerDark};">Subject / last error</th>
            </tr>
            """);

        var items = d.FailedQueueItems;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var bb = i == items.Count - 1 ? "" : "border-bottom:1px solid #ffe3e0;";
            sb.Append($"""
                <tr>
                <td class="mono" valign="top" style="{bb}padding:9px 12px;font-family:{MonoFont};font-size:12px;color:{Muted};white-space:nowrap;">{it.FailedAt.ToLocalTime():MM-dd&nbsp;HH:mm}</td>
                <td class="mono" valign="top" style="{bb}padding:9px 12px;font-family:{MonoFont};font-size:12px;color:{Text};">{Enc(it.From)}<br><span style="color:{Muted};">→ {Enc(it.To)}</span></td>
                <td valign="top" style="{bb}padding:9px 12px;font-family:{SansFont};font-size:12px;color:{Text};">{Enc(it.Subject)}<br><span class="mono" style="font-family:{MonoFont};font-size:11px;color:{DangerFg};">{Enc(it.LastError)} · {it.RetryCount} attempt(s)</span></td>
                </tr>
                """);
        }
        sb.Append("</table></td></tr>");
    }

    private static void AppendHealth(StringBuilder sb, ReportData d)
    {
        SectionTitle(sb, "Health Checks", null, "24px 28px 8px 28px");
        sb.Append($"""
            <tr><td style="padding:0 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid {Border};">
            <tr bgcolor="{PageBg}">
            <th align="left" style="background-color:{PageBg};border-bottom:1px solid {Border};padding:8px 12px;font-family:{SansFont};font-size:11px;font-weight:700;letter-spacing:0.4px;text-transform:uppercase;color:{Muted};">Component</th>
            <th align="left" width="92" style="background-color:{PageBg};border-bottom:1px solid {Border};padding:8px 12px;font-family:{SansFont};font-size:11px;font-weight:700;letter-spacing:0.4px;text-transform:uppercase;color:{Muted};">Status</th>
            <th align="left" style="background-color:{PageBg};border-bottom:1px solid {Border};padding:8px 12px;font-family:{SansFont};font-size:11px;font-weight:700;letter-spacing:0.4px;text-transform:uppercase;color:{Muted};">Detail</th>
            </tr>
            """);

        for (int i = 0; i < d.Health.Count; i++)
        {
            var h = d.Health[i];
            var bb = i == d.Health.Count - 1 ? "" : $"border-bottom:1px solid {Hair};";
            var (label, cfg, cbg, cbd) = Chip(h.Status);
            sb.Append($"""
                <tr>
                <td style="{bb}padding:9px 12px;font-family:{SansFont};font-size:13px;color:{Text};">{Enc(h.Component)}</td>
                <td style="{bb}padding:9px 12px;"><span style="font-family:{SansFont};font-size:11px;font-weight:600;color:{cfg};background-color:{cbg};border:1px solid {cbd};padding:2px 8px;">{label}</span></td>
                <td class="mono" style="{bb}padding:9px 12px;font-family:{MonoFont};font-size:12px;color:{Muted};">{Enc(h.Detail)}</td>
                </tr>
                """);
        }
        sb.Append("</table></td></tr>");
    }

    private static void AppendStatistics(StringBuilder sb, ReportData d)
    {
        SectionTitle(sb, "Email Statistics", $"{DaysLabel(d)}, vs. previous period", "24px 28px 8px 28px");
        sb.Append("""<tr><td style="padding:8px 28px 0 28px;"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr>""");

        StatCard(sb, "Delivered", FormatCount(d.Delivered), Delta(d.Delivered, d.PrevDelivered, lowerIsBetter: false), Text, "0 6px 12px 0");
        StatCard(sb, "Failed (events)", FormatCount(d.Failed), Delta(d.Failed, d.PrevFailed, lowerIsBetter: true), Text, "0 6px 12px 6px");
        var rate = d.SuccessRatePercent.HasValue ? $"{d.SuccessRatePercent.Value:F1}%" : "—";
        StatCard(sb, "Success rate", rate, ($"{FormatCount(d.Delivered)} / {FormatCount(d.Delivered + d.Failed)}", Muted), OkFg, "0 0 12px 6px");

        sb.Append("</tr><tr>");
        var avg = d.AvgDeliveryMs.HasValue ? $"{d.AvgDeliveryMs.Value:F0}<span style=\"font-size:13px;color:{Muted};\"> ms</span>" : "—";
        var peak = d.PeakDeliveryMs.HasValue ? $"peak {FormatMs(d.PeakDeliveryMs.Value)}" : "no data";
        StatCardRaw(sb, "Avg delivery", avg, (peak, Muted), Text, "0 6px 12px 0");
        StatCard(sb, "Distinct senders", d.DistinctSenders.ToString(CultureInfo.InvariantCulture), ("by From address", Muted), Text, "0 6px 12px 6px");
        var vol = FormatBytes(d.VolumeBytes);
        var perMsg = d.Delivered > 0 ? $"avg {FormatBytes(d.VolumeBytes / d.Delivered)} / msg" : "—";
        StatCardRaw(sb, "Volume", vol, (perMsg, Muted), Text, "0 0 12px 6px");

        sb.Append("</tr></table></td></tr>");
    }

    private static void AppendChart(StringBuilder sb, ReportData d)
    {
        if (d.Daily.Count == 0)
            return;

        // Bars use independent scales (failed is tiny next to sent), so surface each peak.
        long sentPeak = d.Daily.Max(p => p.Sent);
        long failedPeak = d.Daily.Max(p => p.Failed);

        sb.Append($"""
            <tr><td style="padding:12px 28px 8px 28px;font-family:{SansFont};">
            <div style="font-size:11px;font-weight:700;letter-spacing:0.6px;text-transform:uppercase;color:{Muted};border-bottom:1px solid {Hair};padding-bottom:6px;">Daily Volume
            <span style="font-weight:400;text-transform:none;letter-spacing:0;color:{Faint};"> &nbsp;— <span style="display:inline-block;width:9px;height:9px;background-color:{Accent};border:1px solid {Accent};">&nbsp;</span> sent (peak {sentPeak}/day) &nbsp;<span style="display:inline-block;width:9px;height:9px;background-color:{DangerFg};border:1px solid {DangerFg};">&nbsp;</span> failed (peak {failedPeak}/day, own scale)</span></div>
            </td></tr>
            <tr><td style="padding:14px 28px 0 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid {Border};"><tr><td style="padding:8px;">
            <img src="cid:{ChartContentId}" width="600" alt="Daily volume — sent vs failed per day" style="display:block;width:100%;max-width:600px;height:auto;border:0;outline:none;text-decoration:none;">
            </td></tr></table></td></tr>
            """);
    }

    private static void AppendTopTable(StringBuilder sb, string title, string subtitle, IReadOnlyList<NamedCount> rows, string barColor)
    {
        SectionTitle(sb, title, subtitle, "24px 28px 8px 28px");
        sb.Append($"""<tr><td style="padding:0 28px;"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid {Border};">""");

        if (rows.Count == 0)
        {
            sb.Append($"""<tr><td class="mono" style="padding:10px 12px;font-family:{MonoFont};font-size:12px;color:{Faint};">No data for this period.</td></tr>""");
        }
        else
        {
            long max = rows.Max(r => r.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var bb = i == rows.Count - 1 ? "" : $"border-bottom:1px solid {Hair};";
                var pct = max > 0 ? (int)Math.Round(r.Count * 100.0 / max) : 0;
                sb.Append($"""
                    <tr>
                    <td class="mono" valign="top" style="{bb}padding:8px 12px;font-family:{MonoFont};font-size:12px;color:{Text};">{Enc(r.Name)}</td>
                    <td class="mono" align="right" valign="top" width="70" style="{bb}padding:8px 12px;font-family:{MonoFont};font-size:12px;color:{Text};font-weight:700;">{FormatCount(r.Count)}</td>
                    <td valign="top" width="180" style="{bb}padding:10px 12px;"><table role="presentation" width="{Math.Max(pct, 3)}%" cellpadding="0" cellspacing="0" border="0"><tr><td bgcolor="{barColor}" height="6" style="background-color:{barColor};height:6px;font-size:0;line-height:0;width:100%;">&nbsp;</td></tr></table></td>
                    </tr>
                    """);
            }
        }
        sb.Append("</table></td></tr>");
    }

    private static void AppendSystem(StringBuilder sb, ReportData d)
    {
        SectionTitle(sb, "System &amp; Performance", null, "24px 28px 8px 28px");
        var mem = d.MemAvgMb.HasValue ? $"{d.MemAvgMb.Value:F0} MB / {d.MemPeakMb.GetValueOrDefault():F0} MB" : "—";
        var cpu = d.CpuAvgPct.HasValue ? $"{d.CpuAvgPct.Value:F1}% / {d.CpuPeakPct.GetValueOrDefault():F1}%" : "—";
        var disk = d.DiskFreePct.HasValue ? $"{d.DiskFreePct.Value:F1}%" : "—";
        sb.Append($"""
            <tr><td style="padding:0 28px 4px 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid {Border};">
            <tr>
            <td width="50%" style="border-bottom:1px solid {Hair};border-right:1px solid {Hair};padding:10px 14px;font-family:{SansFont};font-size:12px;color:{Muted};">Service uptime<br><span class="mono" style="font-family:{MonoFont};font-size:14px;color:{Text};">{Enc(d.Uptime)}</span></td>
            <td width="50%" style="border-bottom:1px solid {Hair};padding:10px 14px;font-family:{SansFont};font-size:12px;color:{Muted};">Memory (avg / peak)<br><span class="mono" style="font-family:{MonoFont};font-size:14px;color:{Text};">{mem}</span></td>
            </tr><tr>
            <td style="border-right:1px solid {Hair};padding:10px 14px;font-family:{SansFont};font-size:12px;color:{Muted};">CPU (avg / peak)<br><span class="mono" style="font-family:{MonoFont};font-size:14px;color:{Text};">{cpu}</span></td>
            <td style="padding:10px 14px;font-family:{SansFont};font-size:12px;color:{Muted};">Disk free<br><span class="mono" style="font-family:{MonoFont};font-size:14px;color:{Text};">{disk}</span></td>
            </tr></table></td></tr>
            """);
    }

    /// <summary>
    /// Friendly, low-key hints about opt-in features that are off (update check, telemetry).
    /// Rendered in the neutral info palette — never a warning chip — and omitted completely
    /// when there is nothing to suggest, so an all-enabled install never sees this box.
    /// </summary>
    private static void AppendRecommendations(StringBuilder sb, ReportData d)
    {
        if (d.Recommendations.Count == 0)
            return;

        SectionTitle(sb, "Recommendations", "suggestions for this installation", "24px 28px 8px 28px");
        sb.Append($"""
            <tr><td style="padding:0 28px 20px 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="{EmailTheme.InfoBg}" style="background-color:{EmailTheme.InfoBg};border:1px solid {EmailTheme.InfoBorder};">
            <tr><td style="padding:12px 14px;font-family:{SansFont};font-size:12px;color:{Text};line-height:18px;">
            """);

        // Grouped by severity (the engine already returns them in that order) so a longer list
        // stays scannable: an operator can read the High block and stop there.
        var first = true;
        foreach (var group in d.Recommendations.GroupBy(r => r.Severity))
        {
            sb.Append($"""
                <div style="{(first ? "" : "padding-top:14px;")}font-family:{MonoFont};font-size:10px;letter-spacing:0.6px;text-transform:uppercase;color:{Muted};">{Enc(group.First().SeverityLabel)} priority</div>
                """);
            first = false;

            foreach (var r in group)
                sb.Append($"""
                    <div style="padding-top:8px;"><span style="font-weight:600;color:{EmailTheme.InfoFg};">{Enc(r.Title)}</span><br>{Enc(r.Detail)}<br><span style="color:{Muted};"><em>Why it matters:</em> {Enc(r.Impact)}</span><br><span style="color:{Muted};">{Enc(r.Category.ToString())} · ConfigTool → {Enc(r.TargetPageName)}</span></div>
                    """);
        }

        sb.Append($"""
            <div style="padding-top:12px;color:{Muted};">Open the ConfigTool → Recommendations page to act on these, or to hide individual ones permanently.</div>
            </td></tr></table></td></tr>
            """);
    }

    private static void AppendFooter(StringBuilder sb, ReportData d)
    {
        sb.Append($"""
            <tr><td bgcolor="{PageBg}" style="background-color:{PageBg};border-top:1px solid {Border};padding:16px 28px;font-family:{SansFont};font-size:11px;color:{Faint};line-height:16px;">
            Automatically generated by <span class="mono" style="font-family:{MonoFont};color:{Muted};">GraphMailer v{Enc(d.Version)}</span> on <span class="mono" style="font-family:{MonoFont};color:{Muted};">{Enc(d.Host)}</span>.<br>
            Reporting schedule and recipients are managed in the ConfigTool → Notifications page. This is an unmonitored mailbox — do not reply.
            </td></tr>
            """);
    }

    // ── Building blocks ──────────────────────────────────────────────────────

    private static void SectionTitle(StringBuilder sb, string title, string? subtitle, string padding)
    {
        var sub = subtitle is null ? "" : $"""<span style="font-weight:400;text-transform:none;letter-spacing:0;color:{Faint};"> &nbsp;— {Enc(subtitle)}</span>""";
        sb.Append($"""
            <tr><td style="padding:{padding};font-family:{SansFont};">
            <div style="font-size:11px;font-weight:700;letter-spacing:0.6px;text-transform:uppercase;color:{Muted};border-bottom:1px solid {Hair};padding-bottom:6px;">{title}{sub}</div>
            </td></tr>
            """);
    }

    private static void KpiCard(StringBuilder sb, string label, string value, string sub, string valueColor, string padding)
    {
        sb.Append($"""
            <td class="kpi-cell" width="33%" valign="top" style="padding:{padding};">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid {Border};"><tr><td style="padding:12px 14px;font-family:{SansFont};">
            <div style="font-size:11px;color:{Muted};text-transform:uppercase;letter-spacing:0.4px;">{Enc(label)}</div>
            <div class="mono" style="font-family:{MonoFont};font-size:28px;font-weight:700;color:{valueColor};padding-top:2px;">{value}</div>
            <div style="font-size:12px;color:{Muted};padding-top:2px;">{Enc(sub)}</div>
            </td></tr></table></td>
            """);
    }

    private static void StatCard(StringBuilder sb, string label, string value, (string text, string color) sub, string valueColor, string padding)
        => StatCardRaw(sb, label, Enc(value), (Enc(sub.text), sub.color), valueColor, padding);

    private static void StatCardRaw(StringBuilder sb, string label, string valueHtml, (string text, string color) sub, string valueColor, string padding)
    {
        sb.Append($"""
            <td class="kpi-cell" width="33%" valign="top" style="padding:{padding};">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid {Border};"><tr><td style="padding:12px 14px;font-family:{SansFont};">
            <div style="font-size:11px;color:{Muted};text-transform:uppercase;letter-spacing:0.4px;">{Enc(label)}</div>
            <div class="mono" style="font-family:{MonoFont};font-size:24px;font-weight:700;color:{valueColor};padding-top:4px;">{valueHtml}</div>
            <div style="font-size:12px;color:{sub.color};padding-top:2px;">{sub.text}</div>
            </td></tr></table></td>
            """);
    }

    private static (string text, string color) Delta(long current, long prev, bool lowerIsBetter)
    {
        if (prev == 0)
            return ("no prior data", Muted);

        var pct = Math.Abs(current - prev) * 100.0 / prev;
        if (current == prev)
            return ("no change vs prev", Muted);

        var up = current > prev;
        var improved = lowerIsBetter ? !up : up;
        var arrow = up ? "▲" : "▼";
        return ($"{arrow} {pct:F1}% vs prev", improved ? OkFg : DangerFg);
    }

    private static (string label, string fg, string bg, string border) Chip(HealthStatus s) => s switch
    {
        HealthStatus.Ok => ("OK", OkFg, OkBg, OkBorder),
        HealthStatus.Warning => ("WARN", WarnFg, WarnBg, WarnBorder),
        HealthStatus.Error => ("ERROR", DangerFg, DangerBg, DangerBorder),
        _ => ("—", Muted, PageBg, Border),
    };

    // ── Formatting helpers ───────────────────────────────────────────────────

    private static string Preheader(ReportData d)
    {
        var rate = d.SuccessRatePercent.HasValue ? $"{d.SuccessRatePercent.Value:F1}% success" : "no delivery data";
        return d.FailedQueueCount > 0
            ? $"{d.FailedQueueCount} message(s) in failed queue (action required) · {FormatCount(d.Delivered)} delivered · {rate}"
            : $"{FormatCount(d.Delivered)} delivered · {rate} · {d.WarningCount} warning(s)";
    }

    private static string BannerDetail(ReportData d)
    {
        var parts = new List<string>();
        if (d.FailedQueueCount > 0)
            parts.Add($"{d.FailedQueueCount} message(s) exhausted all retries and need manual review (see below).");
        foreach (var h in d.Health.Where(h => h.Status is HealthStatus.Error or HealthStatus.Warning))
            parts.Add(Enc($"{h.Component}: {h.Detail}"));
        return string.Join(" ", parts.Take(4));
    }

    private static string DaysLabel(ReportData d) => d.PeriodLabel;

    private static string FormatCount(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    private static string FormatMs(double ms) => ms >= 1000 ? $"{ms / 1000.0:F1} s" : $"{ms:F0} ms";

    internal static string Enc(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
