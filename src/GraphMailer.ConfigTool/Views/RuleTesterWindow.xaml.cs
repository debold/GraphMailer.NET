using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Infrastructure.Rules;
using GraphMailer.Service.Services;
using Microsoft.Win32;
using MimeKit;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Runs the configured rules against a message the operator describes.
///
/// It calls <see cref="MessageRuleProcessor.Run"/> — the exact entry point the service uses — so
/// what it reports is what the relay will do. There is no second evaluation path to keep in sync.
///
/// The layout follows a mail client: message properties, then sender, recipients, subject and
/// attachments, then the content in tabs. Transport is a group of its own because none of it is
/// part of the message.
/// </summary>
public partial class RuleTesterWindow : Window
{
    private readonly Func<ConfigDocument.MessageRulesSection> _section;
    private readonly Func<IReadOnlyList<int>> _configuredPorts;

    /// <summary>Loaded .eml kept as bytes, so every run re-parses a pristine message.</summary>
    private byte[]? _loadedEml;

    /// <summary>True while the client IP was filled from a metadata file and must not be edited.</summary>
    private bool _clientIpFromFile;

    private bool _loading;

    /// <param name="section">
    /// The rules as they stand on the page, read fresh on every run — testing a rule you have
    /// just edited is the whole point, so a snapshot taken when the window opened would be wrong.
    /// </param>
    internal RuleTesterWindow(
        Func<ConfigDocument.MessageRulesSection> section,
        Func<IReadOnlyList<int>> configuredPorts)
    {
        _section = section;
        _configuredPorts = configuredPorts;
        InitializeComponent();

        ClampToScreen();
        RefreshPortPicker();
    }

    /// <summary>
    /// Keeps the window inside the screen's working area.
    ///
    /// A fixed size larger than the display left the lower half — including the result — off
    /// screen with no way to scroll to it, which made the whole tester unusable rather than
    /// merely cramped. The two halves inside are star-sized and scroll on their own, so shrinking
    /// the window costs reachability of nothing.
    /// </summary>
    private void ClampToScreen()
    {
        var work = SystemParameters.WorkArea;

        MaxHeight = work.Height;
        MaxWidth = work.Width;

        // Leave a margin so the window is obviously a window, not a full-screen takeover.
        Height = Math.Min(Height, Math.Max(MinHeight, work.Height - 60));
        Width = Math.Min(Width, Math.Max(MinWidth, work.Width - 60));
    }

    /// <summary>
    /// Caps the input half only when the window is too short to show it whole.
    ///
    /// With no cap it would take its natural height and squeeze the tabs away; with a fixed share
    /// it would scroll even on a large screen, hiding fields the operator is about to fill in.
    /// The cap is therefore a ceiling, not a quota — below it the row keeps its own height.
    /// </summary>
    private void SplitGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double MinimumForTabs = 150;
        InputRow.MaxHeight = Math.Max(InputRow.MinHeight, e.NewSize.Height - MinimumForTabs);
    }

    private void RefreshPortPicker()
    {
        var ports = _configuredPorts();

        TestPort.ItemsSource = ports;
        TestPort.IsEnabled = ports.Count > 0;
        TestPort.SelectedItem = ports.FirstOrDefault();

        if (ports.Count == 0)
            TransportHint.Text = "No listener is configured, so the test assumes port 25. "
                                 + "Add one on the Servers & TLS page to pick a real port.";
    }

    /// <summary>
    /// The port to test with. Falls back to 25 only when no listener is configured at all —
    /// otherwise the tester would report a session that could not happen.
    /// </summary>
    private int SelectedTestPort => TestPort.SelectedItem as int? ?? 25;

    private static string SelectedTag(ComboBox box)
        => box.SelectedItem is ComboBoxItem item ? item.Tag as string ?? string.Empty : string.Empty;

    private static void SelectByTag(ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if ((item.Tag as string ?? string.Empty).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    // ---------------------------------------------------------------- message properties

    /// <summary>Signed and encrypted are mutually exclusive shapes; a message is one or the other.</summary>
    private void Signed_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (TestSigned.IsChecked == true) TestEncrypted.IsChecked = false;
    }

    private void Encrypted_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (TestEncrypted.IsChecked == true) TestSigned.IsChecked = false;
    }


    private SampleProtection SelectedProtection =>
        TestEncrypted.IsChecked == true ? SampleProtection.Encrypted
        : TestSigned.IsChecked == true ? SampleProtection.Signed
        : SampleProtection.None;

    private MessageImportance SelectedImportance => SelectedTag(TestImportance) switch
    {
        "Low" => MessageImportance.Low,
        "High" => MessageImportance.High,
        _ => MessageImportance.Normal,
    };

    // ---------------------------------------------------------------- input

    private void LoadEml_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load a message to test against",
            Filter = "Stored messages (*.eml;*.meta.json)|*.eml;*.meta.json"
                   + "|Mail messages (*.eml)|*.eml"
                   + "|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // Either half of the pair may be picked, and either half may be missing — half a pair
            // is a normal thing to find in a mail folder.
            var pair = MailPairLocator.Resolve(dialog.FileName);
            var metadata = pair.HasMetadata ? TryReadMetadata(pair.MetaPath!) : null;

            if (!pair.HasMessage)
            {
                LoadMetadataOnly(pair.MetaPath!, metadata);
                return;
            }

            var bytes = File.ReadAllBytes(pair.EmlPath!);
            using var stream = new MemoryStream(bytes, writable: false);
            var message = MimeMessage.Load(stream);

            _loadedEml = bytes;
            FillFormFrom(message);

            if (metadata is not null)
                FillEnvelopeFrom(metadata);

            _clientIpFromFile = metadata is not null && !string.IsNullOrWhiteSpace(metadata.ClientIp);
            ApplyLockState(messageFromFile: true);

            ShowBanner(
                $"Showing {Path.GetFileName(pair.EmlPath!)}. Everything that came from the file is locked — "
                + "the test runs against the file itself, so its exact structure is preserved. "
                + "Use “Edit as form” to keep this content and change it."
                + Environment.NewLine
                + (metadata is not null
                    ? "Sender, recipients and client IP come from the metadata file, so they are the real SMTP "
                      + "envelope. Port, TLS and authentication are not stored with a message — set them here."
                    : "No metadata file was found next to it, so sender and recipients were taken from the "
                      + "message headers. Those can differ from the SMTP envelope, which is what actually "
                      + "decides delivery — a blind copy, for instance, appears in neither header."));
        }
        catch (Exception ex)
        {
            _loadedEml = null;
            _clientIpFromFile = false;
            ApplyLockState(messageFromFile: false);
            MessageBox.Show(this,
                $"The file could not be read as a mail message.{Environment.NewLine}{ex.Message}",
                "Load message", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// A sidecar whose message is gone — an orphan from an interrupted write, or a metadata file
    /// copied on its own.
    ///
    /// Still worth loading rather than refusing: the sidecar carries the envelope and the client,
    /// which is precisely what the form cannot otherwise get right. The body and attachments stay
    /// editable, because there is no file to be faithful to.
    /// </summary>
    private void LoadMetadataOnly(string metaPath, MailMetadata? metadata)
    {
        if (metadata is null)
        {
            MessageBox.Show(this,
                "This metadata file could not be read, and no message file was found next to it.",
                "Load message", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _loadedEml = null;
        FillEnvelopeFrom(metadata);
        if (!string.IsNullOrWhiteSpace(metadata.Subject))
            TestSubject.Text = metadata.Subject;

        _clientIpFromFile = !string.IsNullOrWhiteSpace(metadata.ClientIp);
        ApplyLockState(messageFromFile: false, envelopeFromFile: true);

        ShowBanner(
            $"Loaded {Path.GetFileName(metaPath)} — the message file it belongs to is not there. "
            + "Sender, recipients, client IP and subject come from it and are locked; describe the body, "
            + "headers and any attachments here. The test then runs against the message built from these fields.");
    }

    /// <summary>
    /// Reads the sidecar the service writes next to a stored message. Returns
    /// <see langword="null"/> when it cannot be read — an unreadable sidecar means the headers
    /// are used instead, which is worth saying but not worth refusing the message over.
    /// </summary>
    private static MailMetadata? TryReadMetadata(string metaPath)
    {
        try
        {
            return JsonSerializer.Deserialize<MailMetadata>(File.ReadAllText(metaPath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the envelope and client facts from the sidecar.
    ///
    /// This is more than convenience: the envelope is what decides delivery, and it is <b>not</b>
    /// derivable from the message. The headers name a different set — a blind copy appears in
    /// neither, and a To: header may list an address the sender never issued RCPT TO for.
    ///
    /// Listener port, TLS and authentication are deliberately left alone: the sidecar does not
    /// carry them, and silently keeping the previous values is better than inventing some.
    /// </summary>
    private void FillEnvelopeFrom(MailMetadata meta)
    {
        if (!string.IsNullOrWhiteSpace(meta.From))
            TestFrom.Text = meta.From;

        if (meta.To.Count > 0)
        {
            // Split the envelope across the three boxes the way delivery reads it: an address the
            // To or Cc header names goes there, and everything left over is a blind copy. This is
            // the only way a stored message shows who really received it — a Bcc recipient is
            // invisible in the message by definition.
            var inTo = new HashSet<string>(
                TestRecipients.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            var inCc = new HashSet<string>(
                TestCc.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var to = meta.To.Where(inTo.Contains).ToList();
            var cc = meta.To.Where(inCc.Contains).ToList();
            var bcc = meta.To.Where(r => !inTo.Contains(r) && !inCc.Contains(r)).ToList();

            // A sidecar without a message has no headers to split against; everything is then a
            // plain recipient rather than being called a blind copy it may not be.
            if (to.Count == 0 && cc.Count == 0)
            {
                to = [.. meta.To];
                bcc = [];
            }

            TestRecipients.Text = string.Join(Environment.NewLine, to);
            TestCc.Text = string.Join(Environment.NewLine, cc);
            TestBcc.Text = string.Join(Environment.NewLine, bcc);
        }

        if (!string.IsNullOrWhiteSpace(meta.ClientIp))
            TestClientIp.Text = meta.ClientIp;
    }

    /// <summary>
    /// Mirrors a loaded message into the form. Every field, not just the headers — an operator
    /// looking at empty body and attachment boxes has no way to tell whether the message really
    /// has none or the tester simply did not show them.
    /// </summary>
    private void FillFormFrom(MimeMessage message)
    {
        _loading = true;
        try
        {
            var split = MimeMessageSplitter.Split(message);

            TestFrom.Text = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
            TestRecipients.Text = string.Join(Environment.NewLine, message.To.Mailboxes.Select(m => m.Address));
            TestCc.Text = string.Join(Environment.NewLine, message.Cc.Mailboxes.Select(m => m.Address));
            // A blind copy is in no header, so a message on its own cannot reveal one. The
            // metadata file can, and fills this in afterwards when it is there.
            TestBcc.Text = string.Empty;
            TestSubject.Text = message.Subject ?? string.Empty;
            TestBodyText.Text = split.TextBody?.Text ?? string.Empty;
            TestBodyHtml.Text = split.HtmlBody?.Text ?? string.Empty;

            TestAttachments.Text = SampleMessageBuilder.FormatAttachments(
                split.Attachments.Select(a => (
                    Name: MessageRuleEvaluator.FileNameOf(a.Entity),
                    SizeBytes: MimeMessageSplitter.MeasureEncodedSize(a.Entity))));

            TestHeaders.Text = SampleMessageBuilder.FormatHeaders(
                message.Headers.Select(h => (h.Field, h.Value ?? string.Empty)));

            // The properties are derived from the message, so they show what it really is rather
            // than whatever was selected before.
            SelectByTag(TestImportance, MessageRuleEvaluator.ImportanceToken(message));

            var protection = MimeProtection.Classify(message);
            TestSigned.IsChecked = protection == MimeProtectionKind.Signed;
            TestEncrypted.IsChecked = protection == MimeProtectionKind.Encrypted;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Locks exactly the inputs whose value came from a file, and nothing else.
    ///
    /// That is the whole rule, and it answers the obvious question about the transport group: the
    /// client IP <i>is</i> stored in the metadata file, so it locks with the rest; the port, TLS
    /// and the authentication flag are stored nowhere, so they always stay editable. Varying them
    /// against a fixed message is exactly what the tester is for.
    /// </summary>
    private void ApplyLockState(bool messageFromFile, bool envelopeFromFile = false)
    {
        var envelopeLocked = messageFromFile || envelopeFromFile;

        TestFrom.IsReadOnly = envelopeLocked;
        TestRecipients.IsReadOnly = envelopeLocked;
        TestCc.IsReadOnly = envelopeLocked;
        TestBcc.IsReadOnly = envelopeLocked;
        TestSubject.IsReadOnly = envelopeLocked;

        TestBodyText.IsReadOnly = messageFromFile;
        TestBodyHtml.IsReadOnly = messageFromFile;
        TestHeaders.IsReadOnly = messageFromFile;
        TestAttachments.IsReadOnly = messageFromFile;

        // Derived from the message's structure, so they cannot be chosen while it is loaded.
        TestImportance.IsEnabled = !messageFromFile;
        TestSigned.IsEnabled = !messageFromFile;
        TestEncrypted.IsEnabled = !messageFromFile;

        TestClientIp.IsReadOnly = _clientIpFromFile;

        EditAsFormButton.IsEnabled = envelopeLocked || messageFromFile || _clientIpFromFile;
    }

    /// <summary>
    /// Detaches from the loaded file and hands its content back as an editable form.
    ///
    /// Deliberately keeps the field values: they already describe the loaded message, so "load,
    /// look, then change one thing" costs nothing to retype. What is lost is the file's exact
    /// structure — from here on the message is rebuilt from these fields, which is why this is a
    /// separate, explicit step rather than something an edit triggers silently.
    /// </summary>
    private void EditAsForm_Click(object sender, RoutedEventArgs e)
    {
        _loadedEml = null;
        _clientIpFromFile = false;
        ApplyLockState(messageFromFile: false);

        ShowBanner(
            "The fields now describe the message and can be edited. The message is rebuilt from "
            + "them, so anything the file carried beyond these fields is no longer part of the test.");
    }

    private void ShowBanner(string text)
    {
        LoadedFileText.Text = text;
        LoadedBanner.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- the run

    private void RunTest_Click(object sender, RoutedEventArgs e)
    {
        var options = MessageRuleModel.ToOptions(_section());

        // The tester answers "what would the service do", so the master switch counts. Testing
        // rules while the engine is off would report an effect that never happens.
        if (!options.Enabled)
        {
            ShowResult("Message rules are switched off, so no rule runs. "
                       + "Enable them on the Message Rules page to test.");
            return;
        }

        if (SimulateEnforce.IsChecked == true)
        {
            // Projected into the options rather than handled in a second code path — the run
            // below is still the service's own.
            options = new MessageRulesOptions
            {
                Enabled = options.Enabled,
                MaxBodyScanBytes = options.MaxBodyScanBytes,
                RegexTimeoutMs = options.RegexTimeoutMs,
                StoreDiscardedMessages = options.StoreDiscardedMessages,
                DiscardRecordRetentionDays = options.DiscardRecordRetentionDays,
                Rules = [.. options.Rules.Select(AsEnforcing)],
            };
        }

        var to = SampleMessageBuilder.ParseRecipients(TestRecipients.Text);
        var cc = SampleMessageBuilder.ParseRecipients(TestCc.Text);
        var bcc = SampleMessageBuilder.ParseRecipients(TestBcc.Text);

        // The envelope is everyone the message is delivered to, whichever box named them. That is
        // the whole difference between Bcc and the other two: it appears here and in no header.
        var recipients = to.Concat(cc).Concat(bcc)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            ShowResult("Enter at least one recipient in To, Cc or Bcc.");
            return;
        }

        // Re-parsed on every run, so a second test against a loaded file behaves like the first.
        var message = _loadedEml is { } eml
            ? MimeMessage.Load(new MemoryStream(eml, writable: false))
            : SampleMessageBuilder.Build(
                TestFrom.Text.Trim(),
                to,
                TestSubject.Text,
                TestBodyText.Text,
                TestBodyHtml.Text,
                SampleMessageBuilder.ParseAttachments(TestAttachments.Text),
                SampleMessageBuilder.ParseHeaders(TestHeaders.Text),
                SelectedImportance,
                SelectedProtection,
                cc);

        var session = new RuleSessionFacts
        {
            ClientIp = TestClientIp.Text.Trim(),
            ListenerPort = SelectedTestPort,
            Authenticated = TestAuthenticated.IsChecked == true,
            AuthUser = TestAuthenticated.IsChecked == true ? "test-user" : string.Empty,
            Tls = TestTls.IsChecked == true,
        };

        var envelopeFrom = TestFrom.Text.Trim();
        var before = MessageSnapshot.Capture(message, envelopeFrom, recipients);

        var ctx = MessageRuleContext.FromMessage(
            message, envelopeFrom, recipients, session,
            sizeBytes: Measure(message), maxBodyScanBytes: options.MaxBodyScanBytes);

        var outcome = MessageRuleProcessor.Run(options, ctx, RulePolicyLimits.None, explain: true);

        var after = MessageSnapshot.Capture(ctx.Message, ctx.EnvelopeFrom, ctx.EnvelopeRecipients);

        ShowResult(Report(outcome, ctx, before, after));
    }

    /// <summary>
    /// Shows the result and brings its tab forward. Running a test and leaving the answer on a tab
    /// nobody is looking at is the same as not showing it.
    /// </summary>
    private void ShowResult(string text)
    {
        TestResultText.Text = text;
        ContentTabs.SelectedItem = ResultTab;
    }

    /// <summary>Real size, so a message-size condition can be tested.</summary>
    private static long Measure(MimeMessage message)
    {
        using var counter = new MemoryStream();
        message.WriteTo(counter);
        return counter.Length;
    }

    private static MessageRule AsEnforcing(MessageRule rule) => new()
    {
        Enabled = rule.Enabled,
        Name = rule.Name,
        Description = rule.Description,
        Mode = MessageRuleMode.Enforce,
        Match = rule.Match,
        Conditions = rule.Conditions,
        Actions = rule.Actions,
        StopProcessing = rule.StopProcessing,
    };

    // ---------------------------------------------------------------- the report

    private static string Report(
        MessageRuleOutcome outcome, MessageRuleContext ctx, MessageSnapshot before, MessageSnapshot after)
    {
        var sb = new StringBuilder();

        sb.AppendLine("VERDICT");
        sb.AppendLine(outcome.Verdict switch
        {
            RuleVerdict.Reject =>
                $"  Refused: {outcome.SmtpCode} {outcome.SmtpText}  (rule: {outcome.DecidingRule})",
            RuleVerdict.Discard =>
                $"  Discarded: the sending application is told 250, nothing is queued  (rule: {outcome.DecidingRule})",
            _ => "  Accepted and queued.",
        });

        // Every rule, not only the ones that fired. A rule that does nothing is the commonest
        // question about a rule set, and it cannot be answered from a list of the ones that did.
        sb.AppendLine();
        sb.AppendLine("RULES");
        if (outcome.Evaluated.Count == 0)
        {
            sb.AppendLine("  No rules are configured.");
        }
        else
        {
            foreach (var rule in outcome.Evaluated)
                AppendRule(sb, outcome, rule);
        }

        if (outcome.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("WARNINGS");
            foreach (var warning in outcome.Warnings)
                sb.AppendLine($"  • {warning}");
        }

        // Only what actually changed. Printing the whole message twice buried the one thing worth
        // seeing under a dozen identical lines.
        var changes = MessageSnapshot.Diff(before, after);
        sb.AppendLine();
        sb.AppendLine("CHANGES");
        if (changes.Count == 0)
        {
            sb.AppendLine("  The message and envelope are unchanged.");
        }
        else
        {
            var width = changes.Max(c => c.Field.Length);
            foreach (var change in changes)
            {
                sb.AppendLine($"  {change.Field.PadRight(width)}   {change.Before}");
                sb.AppendLine($"  {new string(' ', width)}   →  {change.After}");
            }
        }

        if (after.NotDelivered.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("NOT DELIVERED TO");
            sb.AppendLine($"  {string.Join(", ", after.NotDelivered)}");
            sb.AppendLine("  (named in a header, but not in the envelope — Microsoft 365 drops these)");
        }

        // The single most surprising thing about delivery: on a message with both renderings,
        // only the HTML one reaches the recipient.
        var split = MimeMessageSplitter.Split(ctx.Message);
        sb.AppendLine();
        sb.AppendLine("DELIVERY");
        sb.AppendLine(split.HtmlBody is not null
            ? "  Microsoft 365 delivers this as HTML."
              + (split.TextBody is not null ? " The plain-text version is not delivered." : string.Empty)
            : "  Microsoft 365 delivers this as plain text.");

        return sb.ToString().TrimEnd();
    }

    private static void AppendRule(StringBuilder sb, MessageRuleOutcome outcome, RuleEvaluation rule)
    {
        switch (rule.Status)
        {
            case RuleEvaluationStatus.Matched:
                var summary = outcome.Matched.FirstOrDefault(m => m.Name == rule.Name);
                sb.AppendLine($"  ✓ {rule.Name}  [{rule.Mode}]  → {summary.Outcome}"
                              + (summary.StoppedProcessing ? "  (stops here)" : string.Empty));

                foreach (var action in outcome.Actions.Where(a => a.RuleName == rule.Name))
                {
                    var mark = action.Applied ? "applied" : "would  ";
                    var skip = action.SkipReason is null ? string.Empty : $"  — skipped: {action.SkipReason}";
                    sb.AppendLine($"        {mark}  {action.Detail}{skip}");
                }
                break;

            case RuleEvaluationStatus.NotMatched:
                sb.AppendLine($"  ✗ {rule.Name}  [{rule.Mode}]  → did not match");
                sb.AppendLine($"        {rule.Reason}");
                break;

            case RuleEvaluationStatus.Disabled:
                sb.AppendLine($"  – {rule.Name}  → switched off");
                break;

            case RuleEvaluationStatus.NotReached:
                sb.AppendLine($"  – {rule.Name}  → not reached ({rule.Reason})");
                break;
        }
    }
}
