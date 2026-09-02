using System.Text;

namespace GraphMailer.ConfigTool.Services;

/// <summary>
/// Builds the Exchange Online PowerShell script that grants a relay mailbox SendAs on the
/// recipients GraphMailer has to send for.
///
/// This exists because Microsoft Graph can neither set nor read SendAs — Microsoft states as
/// much ("It's not currently possible to use Microsoft Graph to query which mailboxes the
/// authenticated user has permissions for"). Driving Exchange Online PowerShell from the
/// ConfigTool would mean granting the app Exchange.ManageAsApp plus an Exchange admin role,
/// which is far more privilege than a mail relay should hold. So the tool writes the script
/// and the admin runs it.
///
/// The script has to survive a real tenant: recipient types that cannot be listed at all, objects
/// Exchange reports as inconsistent, and mail users pointing at addresses outside the accepted
/// domains — none of which may stop the run or bury the useful output in red text.
///
/// It also has to reach every group type the sender directory recognises. Graph reports all
/// mail-enabled groups in one collection; Exchange splits them across three cmdlets, and missing
/// one of them means a sender validation accepts and then cannot deliver.
///
/// Pure string generation — no PowerShell is hosted or executed here.
/// </summary>
internal static class SendAsScriptGenerator
{
    /// <summary>
    /// The re-runnable variant: grants SendAs on every distribution group, mail-enabled public
    /// folder and mail user in the tenant. Preferred because objects created later are covered
    /// by running it again, and because it reaches public folders, which Graph cannot list.
    /// </summary>
    internal static string GenerateForAllObjects(string relayMailbox, string version, DateTime utcNow)
    {
        var sb = Header(relayMailbox, version, utcNow);

        sb.AppendLine("""
            # Every mail-enabled object in the tenant. Re-run this after creating new groups or
            # public folders — objects that already have the permission are left alone.
            #
            # A recipient type that cannot be listed is reported and skipped: mail-enabled public
            # folders, for instance, are unavailable in tenants whose public folders live on-premises.
            #
            # Each Get-* cmdlet covers a different recipient type and none of them overlap. In
            # particular Get-DistributionGroup does NOT return Microsoft 365 groups — the kind
            # behind every Team — and Get-UnifiedGroup returns nothing else. Both are needed.
            Grant-All 'Distribution groups'          { Get-DistributionGroup        -ResultSize Unlimited -ErrorAction Stop -WarningAction SilentlyContinue }
            Grant-All 'Microsoft 365 groups'         { Get-UnifiedGroup             -ResultSize Unlimited -ErrorAction Stop -WarningAction SilentlyContinue }
            Grant-All 'Dynamic distribution groups'  { Get-DynamicDistributionGroup -ResultSize Unlimited -ErrorAction Stop -WarningAction SilentlyContinue }
            Grant-All 'Mail-enabled public folders'  { Get-MailPublicFolder         -ResultSize Unlimited -ErrorAction Stop -WarningAction SilentlyContinue }
            Grant-All 'Mail users'                   { Get-MailUser                 -ResultSize Unlimited -ErrorAction Stop -WarningAction SilentlyContinue }
            """);
        sb.AppendLine();

        return Footer(sb).ToString();
    }

    /// <summary>
    /// The narrow variant: grants SendAs only on the listed objects, for tenants where the relay
    /// mailbox must not be able to send as everything. Needs re-generating whenever the set changes.
    /// </summary>
    internal static string GenerateForObjects(
        string relayMailbox, string version, DateTime utcNow, IReadOnlyList<string> objects)
    {
        var sb = Header(relayMailbox, version, utcNow);

        sb.AppendLine("""
            # Only the objects selected in the ConfigTool. Re-generate this script when the set of
            # senders changes — new objects are not picked up automatically.
            $Objects = @(
            """);

        var wanted = objects
            .Select(o => o.Trim())
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var obj in wanted)
            sb.AppendLine($"    '{Escape(obj)}'");

        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("""
            Grant-All 'Selected objects' {
                foreach ($entry in $Objects) {
                    $found = Get-Recipient -Identity $entry -ErrorAction SilentlyContinue
                    if ($found) { $found } else { Write-Warning "not found: $entry" }
                }
            }
            """);
        sb.AppendLine();

        return Footer(sb).ToString();
    }

    private static StringBuilder Header(string relayMailbox, string version, DateTime utcNow)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# ---------------------------------------------------------------------------");
        sb.AppendLine("# GraphMailer.NET - grant SendAs to the relay mailbox");
        sb.AppendLine($"# Generated {utcNow:yyyy-MM-dd HH:mm} UTC by GraphMailer ConfigTool {version}");
        sb.AppendLine("#");
        sb.AppendLine("# Run this in Exchange Online PowerShell as an Exchange administrator:");
        sb.AppendLine("#   Install-Module ExchangeOnlineManagement -Scope CurrentUser");
        sb.AppendLine("#");
        sb.AppendLine("# Exchange replicates permission changes lazily - allow up to an hour before");
        sb.AppendLine("# testing. Until then mail stays queued in GraphMailer and is retried.");
        sb.AppendLine("# ---------------------------------------------------------------------------");
        sb.AppendLine();
        sb.AppendLine($"$Relay = '{Escape(relayMailbox)}'");
        sb.AppendLine();
        sb.AppendLine("Connect-ExchangeOnline");
        sb.AppendLine();
        sb.AppendLine("""
            # Exchange refuses SendAs on an address outside the accepted domains, and says so once
            # per object. Most mail users and every guest fall into that group, so the list is
            # fetched up front and those recipients are skipped with one readable line instead.
            $Accepted = @(Get-AcceptedDomain | ForEach-Object { [string]$_.DomainName })

            $script:Granted = 0
            $script:Existing = 0
            $script:Skipped = 0
            $script:Failed = 0

            # Returns the first address domain Exchange will not accept, or nothing when the
            # recipient is usable. A mail user has to be checked on both of its addresses: the
            # primary one can sit in an accepted domain while the external address it forwards to
            # does not, and Exchange rejects the grant on account of that second one.
            function Get-BlockedDomain {
                param([Parameter(Mandatory)] $Recipient)

                foreach ($candidate in @($Recipient.PrimarySmtpAddress, $Recipient.ExternalEmailAddress)) {
                    $text = [string]$candidate
                    if (-not $text) { continue }

                    # ExternalEmailAddress carries an "SMTP:" prefix; the primary one does not.
                    $domain = (($text -split ':')[-1] -split '@')[-1]
                    if ($domain -and $Accepted -notcontains $domain) { return $domain }
                }

                return $null
            }

            function Grant-SendAs {
                param([Parameter(Mandatory)] $Recipient)

                $name = $Recipient.DisplayName
                $address = [string]$Recipient.PrimarySmtpAddress

                $blocked = Get-BlockedDomain $Recipient
                if ($blocked) {
                    Write-Host "skip   $name <$address> - $blocked is not an accepted domain"
                    $script:Skipped++
                    return
                }

                # Add-RecipientPermission errors out when the right is already there, so check
                # first and keep the script quiet on a repeat run.
                $existing = Get-RecipientPermission -Identity $Recipient.Identity -Trustee $Relay -ErrorAction SilentlyContinue -WarningAction SilentlyContinue
                if ($existing) {
                    $script:Existing++
                    return
                }

                try {
                    # Exchange also writes its object-consistency complaints to the warning stream,
                    # which would repeat verbatim what the catch below already reports.
                    Add-RecipientPermission -Identity $Recipient.Identity -Trustee $Relay -AccessRights SendAs -Confirm:$false -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null
                    Write-Host "grant  $name <$address>"
                    $script:Granted++
                }
                catch {
                    # One object Exchange considers inconsistent must not stop the rest.
                    Write-Warning "failed $name <$address> - $($_.Exception.Message)"
                    $script:Failed++
                }
            }

            function Grant-All {
                param([Parameter(Mandatory)] [string] $Label,
                      [Parameter(Mandatory)] [scriptblock] $Source)

                Write-Host ""
                Write-Host "== $Label"
                try {
                    & $Source | ForEach-Object { Grant-SendAs $_ }
                }
                catch {
                    Write-Warning "$Label could not be listed - $($_.Exception.Message)"
                }
            }
            """);
        sb.AppendLine();

        return sb;
    }

    private static StringBuilder Footer(StringBuilder sb)
    {
        sb.AppendLine("""
            Write-Host ""
            Write-Host "granted $script:Granted, already in place $script:Existing, skipped $script:Skipped, failed $script:Failed"
            Write-Host ""

            # Read back what is actually granted. Graph cannot report this, so this list is the
            # only confirmation that the relay mailbox may send as these objects.
            Get-RecipientPermission -Trustee $Relay | Format-Table Identity, Trustee, AccessRights
            """);
        return sb;
    }

    /// <summary>PowerShell single-quoted string literal: a quote is escaped by doubling it.</summary>
    private static string Escape(string value) => value.Replace("'", "''");
}
