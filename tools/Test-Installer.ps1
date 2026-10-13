#Requires -Version 7.0
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    End-to-end test of the GraphMailer installer: install (or upgrade), exercise the running
    service, uninstall. Built for throwaway machines - GitHub Actions runners or a test VM.

.DESCRIPTION
    Fresh install (default):
      1. Silent install of -Setup.
      2. Checks the installation: one visible and one hidden "GraphMailer" entry in Programs and
         Features with the expected version, the service (auto start, LocalSystem, running),
         installed files and help, both Start-menu shortcuts, the firewall rule, and the
         first-run provisioning (graphmailer.json, self-signed "GraphMailer SMTP" certificate).
      3. Exercises the service: a message over plain SMTP on port 25 must be accepted and queued;
         implicit TLS on port 465 must complete with TLS 1.2+ and present the self-signed
         certificate.
      4. Silent uninstall, then checks that service, program files, shortcuts and firewall rule
         are gone while C:\ProgramData\GraphMailer (configuration, keys, mail) is kept.

    Upgrade (-PreviousSetup):
      Installs -PreviousSetup first and gives it state worth keeping: a marker in the SMTP banner,
      an SMTP user whose password the service captures and stores as ENC[...] (encrypted with the
      machine key ring), and a queued message. After installing -Setup over it, it additionally
      checks that the installation was replaced rather than duplicated, the banner marker is
      served (configuration kept and read by the new version), the captured password still
      authenticates while a wrong one is rejected (Data Protection key ring and decryption survive
      the upgrade), and the queued message is still there.

    The script changes the machine: it installs a Windows service, a certificate, a firewall rule
    and C:\ProgramData\GraphMailer. Outside GitHub Actions it refuses to run without
    -AllowMachineChanges.

.PARAMETER Setup
    GraphMailerSetup-<version>.exe to test.

.PARAMETER ExpectedVersion
    The four-part FileVersion the installed GraphMailer.exe must carry.

.PARAMETER PreviousSetup
    Setup of the previous release. Turns the run into an upgrade test.

.PARAMETER ArtifactDir
    Where setup logs and the GraphMailer logs are copied, for upload when a run fails.

.PARAMETER AllowMachineChanges
    Required outside GitHub Actions, as a deliberate acknowledgement.

.EXAMPLE
    .\tools\Test-Installer.ps1 -Setup .\GraphMailerSetup-1.5.3.1130.exe -ExpectedVersion 1.5.3.1130 -AllowMachineChanges
#>
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [string] $ExpectedVersion,
    [string] $PreviousSetup,
    [string] $ArtifactDir = (Join-Path ([IO.Path]::GetTempPath()) 'gm-installer-test'),
    [switch] $AllowMachineChanges
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $env:GITHUB_ACTIONS -and -not $AllowMachineChanges) {
    throw 'This test installs and removes GraphMailer on this machine. Run it on a throwaway ' +
          'machine and pass -AllowMachineChanges to confirm.'
}

$Setup = (Resolve-Path $Setup).Path
if ($PreviousSetup) { $PreviousSetup = (Resolve-Path $PreviousSetup).Path }
New-Item -ItemType Directory -Path $ArtifactDir -Force | Out-Null

$InstallDir  = Join-Path $env:ProgramFiles 'GraphMailer'
$DataDir     = Join-Path $env:ProgramData 'GraphMailer'
$ConfigFile  = Join-Path $DataDir 'config\graphmailer.json'
$ShortcutDir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\GraphMailer'
$CertSubject = 'CN=GraphMailer SMTP'

$CaptureUser     = 'ci-upgrade'
$CapturePassword = 'Ci-' + [Guid]::NewGuid().ToString('N')
$BannerMarker    = 'GraphMailer-CI-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)

$script:Failures = [System.Collections.Generic.List[string]]::new()

# -- Reporting --------------------------------------------------------------------------------

function Step([string] $text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

function Check([string] $what, [bool] $ok, [string] $detail = '') {
    if ($ok) {
        Write-Host "  [ok]   $what" -ForegroundColor Green
    } else {
        $line = if ($detail) { "$what - $detail" } else { $what }
        Write-Host "  [FAIL] $line" -ForegroundColor Red
        $script:Failures.Add($line)
    }
}

function Wait-Until([scriptblock] $condition, [int] $timeoutSeconds, [string] $what, [int] $intervalMs = 500) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Milliseconds $intervalMs
    }
    Write-Host "  (timed out after ${timeoutSeconds}s waiting for: $what)" -ForegroundColor DarkYellow
    return $false
}

# -- Setup ------------------------------------------------------------------------------------

function Invoke-Setup([string] $exe, [string] $label, [switch] $Uninstall) {
    $log = Join-Path $ArtifactDir "setup-$label.log"
    $arguments = @('/quiet', '/norestart', '/log', $log)
    if ($Uninstall) { $arguments = @('/uninstall') + $arguments }
    Write-Host "  running $(Split-Path $exe -Leaf) $($arguments -join ' ')"
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru
    # 3010 = success, reboot required (never expected here, but not a failure of the installer)
    if ($p.ExitCode -notin 0, 3010) {
        Get-ChildItem $ArtifactDir -Filter "setup-$label*.log" | ForEach-Object {
            Write-Host "---- $($_.Name) (tail)"; Get-Content $_.FullName -Tail 40
        }
        throw "Setup '$label' failed with exit code $($p.ExitCode)."
    }
}

function Get-ArpEntries {
    Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                     'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties.Name -contains 'DisplayName' -and $_.DisplayName -eq 'GraphMailer' }
}

function Test-Hidden($entry) {
    ($entry.PSObject.Properties.Name -contains 'SystemComponent') -and $entry.SystemComponent -eq 1
}

# -- SMTP client (plain or implicit TLS) ------------------------------------------------------

function Open-Smtp([int] $port, [switch] $Tls) {
    $client = [System.Net.Sockets.TcpClient]::new('127.0.0.1', $port)
    $client.ReceiveTimeout = 30000
    $client.SendTimeout = 30000
    $stream = $client.GetStream()
    $ssl = $null
    if ($Tls) {
        # The listener presents the self-signed certificate; trust is not under test here.
        $accept = [System.Net.Security.RemoteCertificateValidationCallback] { param($s, $c, $ch, $e) $true }
        $ssl = [System.Net.Security.SslStream]::new($stream, $false, $accept)
        $ssl.AuthenticateAsClient('localhost')
        $stream = $ssl
    }
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII)
    $writer = [IO.StreamWriter]::new($stream, [Text.Encoding]::ASCII)
    $writer.NewLine = "`r`n"
    $writer.AutoFlush = $true
    $session = [pscustomobject]@{ Client = $client; Ssl = $ssl; Reader = $reader; Writer = $writer; Greeting = $null }
    $session.Greeting = Read-Reply $session
    return $session
}

function Read-Reply($session) {
    $lines = @()
    do {
        $line = $session.Reader.ReadLine()
        if ($null -eq $line) { throw 'SMTP connection closed unexpectedly.' }
        $lines += $line
    } while ($line.Length -ge 4 -and $line[3] -eq '-')
    [pscustomobject]@{ Code = [int]$line.Substring(0, 3); Text = ($lines -join "`n") }
}

function Send-Command($session, [string] $command) {
    $session.Writer.WriteLine($command)
    Read-Reply $session
}

function Close-Smtp($session) {
    try { $null = Send-Command $session 'QUIT' } catch { }
    $session.Client.Dispose()
}

function Send-TestMail([string] $marker) {
    $s = Open-Smtp 25
    try {
        $steps = @(
            @('EHLO ci.example.test', 250),
            @('MAIL FROM:<ci-sender@example.test>', 250),
            @('RCPT TO:<ci-recipient@example.test>', 250),
            @('DATA', 354)
        )
        foreach ($step in $steps) {
            $reply = Send-Command $s $step[0]
            if ($reply.Code -ne $step[1]) { return "'$($step[0])' answered $($reply.Text)" }
        }
        $s.Writer.WriteLine('From: ci-sender@example.test')
        $s.Writer.WriteLine('To: ci-recipient@example.test')
        $s.Writer.WriteLine('Subject: GraphMailer installer test')
        $s.Writer.WriteLine("X-GraphMailer-CI-Marker: $marker")
        $s.Writer.WriteLine('')
        $s.Writer.WriteLine('Queued by tools/Test-Installer.ps1.')
        $reply = Send-Command $s '.'
        if ($reply.Code -ne 250) { return "end of DATA answered $($reply.Text)" }
        return $null
    } finally { Close-Smtp $s }
}

function Find-QueuedMail([string] $marker) {
    # Without Graph credentials the queue processor may already have moved the message on,
    # so any of the mail folders counts: the point is that the relay accepted and stored it.
    $mailDir = Join-Path $DataDir 'mail'
    if (-not (Test-Path $mailDir)) { return $false }
    [bool](Get-ChildItem $mailDir -Recurse -Filter *.eml -ErrorAction SilentlyContinue |
        Select-String -SimpleMatch $marker -List)
}

function Test-SmtpAuth([string] $user, [string] $password) {
    $s = Open-Smtp 465 -Tls
    try {
        $null = Send-Command $s 'EHLO ci.example.test'
        $token = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("`0$user`0$password"))
        return (Send-Command $s "AUTH PLAIN $token").Code
    } finally { Close-Smtp $s }
}

# -- Configuration edits (as an operator would make them) -------------------------------------

function Edit-Config([scriptblock] $change) {
    $json = Get-Content $ConfigFile -Raw | ConvertFrom-Json -AsHashtable
    & $change $json
    $json | ConvertTo-Json -Depth 64 | Set-Content $ConfigFile -Encoding utf8
}

function Get-ConfigUser([string] $name) {
    $json = Get-Content $ConfigFile -Raw | ConvertFrom-Json -AsHashtable
    if (-not $json.ContainsKey('Users')) { return $null }
    $json['Users'] | Where-Object { $_['Username'] -eq $name } | Select-Object -First 1
}

# -- Checks -----------------------------------------------------------------------------------

function Wait-ServiceReady {
    $up = Wait-Until { (Get-Service GraphMailer).Status -eq 'Running' } 120 'service running'
    Check 'service is running' $up
    foreach ($port in 25, 465, 587) {
        $listening = Wait-Until { [bool](Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) } 90 "port $port"
        Check "listening on port $port" $listening
    }
}

function Test-InstalledState([string] $version) {
    $entries = @(Get-ArpEntries)
    $visible = @($entries | Where-Object { -not (Test-Hidden $_) })
    $hidden  = @($entries | Where-Object { Test-Hidden $_ })
    Check 'exactly one visible Programs and Features entry (the setup bundle)' ($visible.Count -eq 1) "found $($visible.Count)"
    Check 'exactly one installed GraphMailer MSI product' ($hidden.Count -eq 1) "found $($hidden.Count)"
    if ($visible.Count -eq 1) {
        Check "Programs and Features shows version $version" ($visible[0].DisplayVersion -eq $version) "shows $($visible[0].DisplayVersion)"
    }

    $exe = Join-Path $InstallDir 'GraphMailer.exe'
    $exeVersion = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.FileVersion } else { '<missing>' }
    Check "GraphMailer.exe has FileVersion $version" ($exeVersion -eq $version) "found $exeVersion"
    Check 'ConfigTool installed' (Test-Path (Join-Path $InstallDir 'GraphMailer.ConfigTool.exe'))
    Check 'help installed' (Test-Path (Join-Path $InstallDir 'help\index.html'))

    $svc = Get-CimInstance Win32_Service -Filter "Name='GraphMailer'"
    Check 'service registered' ($null -ne $svc)
    if ($svc) {
        Check 'service starts automatically' ($svc.StartMode -eq 'Auto') "start mode $($svc.StartMode)"
        Check 'service runs as LocalSystem' ($svc.StartName -eq 'LocalSystem') "account $($svc.StartName)"
        Check 'service runs the installed exe' ($svc.PathName.Trim('"') -eq $exe) "path $($svc.PathName)"
    }

    Check 'Start-menu shortcut: ConfigTool' (Test-Path (Join-Path $ShortcutDir 'GraphMailer Configuration.lnk'))
    Check 'Start-menu shortcut: help' (Test-Path (Join-Path $ShortcutDir 'GraphMailer Help.lnk'))
    Check 'firewall rule registered' ([bool](Get-NetFirewallRule -DisplayName 'GraphMailer SMTP Relay' -ErrorAction SilentlyContinue))
}

function Test-FirstRun {
    Check 'graphmailer.json provisioned' (Wait-Until { Test-Path $ConfigFile } 60 'graphmailer.json')
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq $CertSubject | Select-Object -First 1
    Check 'self-signed SMTP certificate provisioned' ($null -ne $cert)
}

function Test-Smtp([string] $label) {
    $marker = "$label-$([Guid]::NewGuid().ToString('N'))"
    $problem = Send-TestMail $marker
    Check 'plain SMTP on port 25 accepts a message' ($null -eq $problem) $problem
    if ($null -eq $problem) {
        Check 'accepted message is stored by the relay' (Wait-Until { Find-QueuedMail $marker } 30 'queued message')
    }

    $tls = Open-Smtp 465 -Tls
    try {
        $protocol = $tls.Ssl.SslProtocol
        Check 'implicit TLS on port 465 uses TLS 1.2 or newer' ($protocol -in 'Tls12', 'Tls13') "negotiated $protocol"
        Check 'port 465 presents the self-signed certificate' ($tls.Ssl.RemoteCertificate.Subject -eq $CertSubject) `
            "subject $($tls.Ssl.RemoteCertificate.Subject)"
    } finally { Close-Smtp $tls }
    return $marker
}

function Save-Diagnostics {
    $logs = Join-Path $DataDir 'logs'
    if (Test-Path $logs) { Copy-Item $logs (Join-Path $ArtifactDir 'graphmailer-logs') -Recurse -Force -ErrorAction SilentlyContinue }
    Get-ChildItem $env:TEMP -Filter 'GraphMailer*.log' -ErrorAction SilentlyContinue |
        Copy-Item -Destination $ArtifactDir -ErrorAction SilentlyContinue
}

# -- Scenario ---------------------------------------------------------------------------------

try {
    $previousMarker = $null

    if ($PreviousSetup) {
        Step "Install the previous release ($(Split-Path $PreviousSetup -Leaf))"
        Invoke-Setup $PreviousSetup 'previous'
        Wait-ServiceReady
        Test-FirstRun

        Step 'Give the old installation state worth keeping'
        Edit-Config {
            param($c)
            $c['Smtp']['Banner'] = $BannerMarker
            $users = if ($c.ContainsKey('Users') -and $c['Users']) { @($c['Users']) } else { @() }
            $c['Users'] = $users + @(@{ Username = $CaptureUser; DisplayName = 'Installer test'; Enabled = $true; CaptureNextPassword = $true })
        }
        # The access options hot-reload; the first login of the armed user is captured. Until the
        # reload lands, logins fail and count towards IP blocking (10 failures in 10 minutes), so
        # retry slowly and at most 9 times.
        $loggedIn = Wait-Until { (Test-SmtpAuth $CaptureUser $CapturePassword) -eq 235 } 27 'armed user accepted' 3000
        Check 'old version accepts the armed user' $loggedIn
        $captured = Wait-Until {
            $u = Get-ConfigUser $CaptureUser
            $u -and -not $u['CaptureNextPassword'] -and ([string]$u['Password']).StartsWith('ENC[')
        } 30 'captured password written'
        Check 'old version captured the SMTP password and stored it encrypted' $captured
        Check 'old version accepts the captured password' ((Test-SmtpAuth $CaptureUser $CapturePassword) -eq 235)

        $previousMarker = "previous-$([Guid]::NewGuid().ToString('N'))"
        $problem = Send-TestMail $previousMarker
        Check 'old version queues a message' (($null -eq $problem) -and (Wait-Until { Find-QueuedMail $previousMarker } 30 'queued message')) $problem
    }

    Step "Install $(Split-Path $Setup -Leaf)"
    Invoke-Setup $Setup 'install'
    Wait-ServiceReady
    Test-InstalledState $ExpectedVersion
    Test-FirstRun

    if ($PreviousSetup) {
        Step 'Check what the upgrade had to keep'
        $s = Open-Smtp 25
        try { $greeting = $s.Greeting.Text } finally { Close-Smtp $s }
        Check 'configuration kept and read by the new version (banner marker served)' ($greeting -like "*$BannerMarker*") "greeting '$greeting'"
        Check 'captured password still authenticates (key ring and decryption survived)' ((Test-SmtpAuth $CaptureUser $CapturePassword) -eq 235)
        Check 'a wrong password is still rejected' ((Test-SmtpAuth $CaptureUser 'definitely-wrong') -eq 535)
        Check 'mail queued before the upgrade is still there' (Find-QueuedMail $previousMarker)
    }

    Step 'Exercise the running service'
    $null = Test-Smtp 'install'

    Step 'Uninstall'
    Invoke-Setup $Setup 'uninstall' -Uninstall
    Check 'service removed' (Wait-Until { -not (Get-Service GraphMailer -ErrorAction SilentlyContinue) } 60 'service removal')
    Check 'program folder removed' (-not (Test-Path (Join-Path $InstallDir 'GraphMailer.exe')))
    Check 'Start-menu shortcuts removed' (-not (Test-Path $ShortcutDir))
    Check 'firewall rule removed' (-not (Get-NetFirewallRule -DisplayName 'GraphMailer SMTP Relay' -ErrorAction SilentlyContinue))
    Check 'no Programs and Features entry left' (@(Get-ArpEntries).Count -eq 0) "found $(@(Get-ArpEntries).Count)"
    Check 'configuration kept in ProgramData' (Test-Path $ConfigFile)
}
catch {
    $script:Failures.Add("aborted: $($_.Exception.Message)")
    Write-Host "  [FAIL] aborted: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
}
finally {
    Save-Diagnostics
}

Write-Host ''
if ($script:Failures.Count -gt 0) {
    Write-Host "$($script:Failures.Count) check(s) failed:" -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "Diagnostics: $ArtifactDir"
    exit 1
}
Write-Host 'All installer checks passed.' -ForegroundColor Green
