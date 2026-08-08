<#
.SYNOPSIS
    Fills a throwaway GraphMailer data directory with enough logs, messages, metrics and IP blocks
    to exercise the paging, search and truncation limits of the ConfigTool by hand.

.DESCRIPTION
    Everything lands under -Path; the real installation under C:\ProgramData\GraphMailer is never
    touched. Start the ConfigTool with GRAPHMAILER_DATA_DIR pointing at the same folder (the script
    prints the exact command) and it reads this data instead of the live one.

    The existing config is copied in so the ConfigTool does not treat the sandbox as a first run.
    Secrets stay readable: they are encrypted with the machine key ring, which is the same either way.

.EXAMPLE
    .\tools\seed-uitest-data.ps1
    .\tools\seed-uitest-data.ps1 -Path D:\gm-uitest -LogEntries 8000 -Messages 1500
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $env:TEMP 'graphmailer-uitest'),

    # 7 files worth of log lines — the page loads 2000 at a time
    [int]$LogEntries = 6000,

    # Messages per folder — the page loads 500 at a time
    [int]$Messages = 1200,

    # email_events rows — Recent Activity loads 500 at a time
    [int]$Events = 1500,

    # Distinct client IPs (Top Client Hosts shows 8) and Graph errors (Top Failure Causes shows 6)
    [int]$DistinctHosts = 20,
    [int]$DistinctErrors = 15,

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

# Seeding metrics.db loads Microsoft.Data.Sqlite from the .NET 10 build output, which Windows
# PowerShell 5.1 (.NET Framework) cannot load. Hand the whole run to pwsh instead of failing
# three quarters of the way through.
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwsh) {
        # Plain ASCII: this line is printed by Windows PowerShell, whose console mangles non-ASCII
        Write-Host 'Windows PowerShell detected - re-running under pwsh (needed for metrics.db).' -ForegroundColor Yellow
        $argList = @()
        foreach ($kv in $PSBoundParameters.GetEnumerator()) {
            if ($kv.Value -is [switch]) {
                if ($kv.Value.IsPresent) { $argList += "-$($kv.Key)" }
            } else {
                $argList += "-$($kv.Key)"
                $argList += "$($kv.Value)"
            }
        }
        & $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @argList
        exit $LASTEXITCODE
    }

    Write-Warning 'Running under Windows PowerShell and pwsh was not found — metrics.db will be skipped.'
}

if ($Clean -and (Test-Path $Path)) {
    Write-Host "Removing $Path ..." -ForegroundColor Yellow
    Remove-Item $Path -Recurse -Force
}

foreach ($sub in 'config', 'logs', 'data', 'mail\queue', 'mail\failed', 'mail\sent') {
    New-Item -ItemType Directory -Force (Join-Path $Path $sub) | Out-Null
}

Write-Host "Seeding $Path" -ForegroundColor Cyan

# ── Config: copy the live one so the ConfigTool does not run its first-run path ──────────────
$liveConfig = 'C:\ProgramData\GraphMailer\config\graphmailer.json'
$destConfig = Join-Path $Path 'config\graphmailer.json'
if ((Test-Path $liveConfig) -and -not (Test-Path $destConfig)) {
    Copy-Item $liveConfig $destConfig
    Write-Host '  config      : copied from the live installation'
} elseif (-not (Test-Path $liveConfig)) {
    Write-Host '  config      : no live config found — the ConfigTool will show defaults' -ForegroundColor Yellow
}

# ── Logs: seven daily rolling files, oldest 6 days back ──────────────────────────────────────
$components = 'SmtpRelay', 'QueueProcessor', 'GraphApi', 'IpBlocking', 'Metrics', 'CertMonitor'
$levels = 'INF', 'INF', 'INF', 'DBG', 'WRN', 'ERR'
$perFile = [math]::Max(1, [int]($LogEntries / 7))
$tz = (Get-Date).ToString('zzz')

for ($d = 6; $d -ge 0; $d--) {
    $day = (Get-Date).AddDays(-$d)
    $file = Join-Path $Path ('logs\graphmailer-{0}.log' -f $day.ToString('yyyyMMdd'))
    $sb = [System.Text.StringBuilder]::new()

    for ($i = 0; $i -lt $perFile; $i++) {
        $ts = $day.Date.AddSeconds($i * [math]::Max(1, [int](86400 / $perFile)))
        $lvl = $levels[$i % $levels.Count]
        $cmp = $components[$i % $components.Count]
        $stamp = '{0} {1}' -f $ts.ToString('yyyy-MM-dd HH:mm:ss.fff'), $tz

        # A marker only in the OLDEST file: proves the search reaches past the loaded page
        $msg = if ($d -eq 6 -and $i -eq 0) {
            'NEEDLE-OLDEST-ENTRY reached the seventh retained file'
        } else {
            "Processed message {0:D5} for recipient user{1}@contoso.com" -f $i, ($i % 40)
        }

        [void]$sb.AppendLine("$stamp [$lvl] [$cmp] $msg")

        # Continuation lines must fold into their entry. The level cycles with period 6 and ERR sits
        # at index 5, so the stride has to be a multiple of 6 offset by 5 — an unrelated stride
        # (200) never lands on an ERR line and silently produced no stack traces at all.
        if ($lvl -eq 'ERR' -and $i % 60 -eq 5) {
            [void]$sb.AppendLine('System.Net.Http.HttpRequestException: The operation timed out')
            [void]$sb.AppendLine('   at GraphMailer.Service.Services.GraphApiClient.SendAsync()')
            [void]$sb.AppendLine('   at GraphMailer.Service.Services.QueueProcessor.ProcessAsync()')
        }
    }

    [System.IO.File]::WriteAllText($file, $sb.ToString())
}
Write-Host "  logs        : 7 files, ~$($perFile * 7) entries (marker NEEDLE-OLDEST-ENTRY in the oldest)"

# ── Messages: *.meta.json pairs across queue / failed / sent ─────────────────────────────────
$errors = @(
    'ErrorInvalidUser: The requested user is invalid',
    'MailboxNotEnabledForRESTAPI: mailbox is hosted on-premises',
    'ErrorMessageSizeExceeded: the message exceeds the maximum size',
    'ErrorAccessDenied: insufficient privileges',
    'ErrorQuotaExceeded: the mailbox is full'
)

function New-Meta {
    param($Dir, $Index, $Status, $Received)

    $meta = [ordered]@{
        MessageId     = [guid]::NewGuid().ToString()
        From          = "app{0}@contoso.com" -f ($Index % 12)
        To            = @("ops{0}@contoso.com" -f ($Index % 25))
        Subject       = if ($Index -eq 0) { 'NEEDLE-OLDEST-MESSAGE' } else { "Nightly Backup Report {0:D5}" -f $Index }
        Status        = $Status
        ReceivedAt    = $Received.ToUniversalTime().ToString('o')
        ClientIp      = '10.0.0.{0}' -f ($Index % 30)
        SmtpMessageId = "<{0}@relay.contoso.com>" -f $Index
        RetryCount    = $Index % 4
    }
    if ($Status -eq 'failed') {
        $meta['LastError'] = $errors[$Index % $errors.Count]
        $meta['LastAttemptAt'] = $Received.AddMinutes(30).ToUniversalTime().ToString('o')
    }
    if ($Status -eq 'sent') { $meta['SentAt'] = $Received.AddMinutes(1).ToUniversalTime().ToString('o') }

    $file = Join-Path $Dir ("{0}.meta.json" -f $meta.MessageId)
    $meta | ConvertTo-Json -Depth 4 | Set-Content $file -Encoding UTF8
}

$now = Get-Date
$split = @{ 'queue' = 0.5; 'failed' = 0.2; 'sent' = 0.3 }
foreach ($folder in $split.Keys) {
    $count = [int]($Messages * $split[$folder])
    $status = switch ($folder) { 'queue' { 'queued' } 'failed' { 'failed' } 'sent' { 'sent' } }

    # Message ids are fresh GUIDs, so without this a second run would stack another full set on
    # top of the first instead of reproducing the same sandbox.
    Get-ChildItem (Join-Path $Path "mail\$folder") -Filter '*.meta.json' -ErrorAction SilentlyContinue |
        Remove-Item -Force

    for ($i = 0; $i -lt $count; $i++) {
        New-Meta -Dir (Join-Path $Path "mail\$folder") -Index $i -Status $status -Received $now.AddMinutes(-$i * 3)
    }
    Write-Host ("  mail\{0}" -f $folder).PadRight(16) ": $count messages"
}

# ── Blocked IPs: the snapshot the service normally publishes ─────────────────────────────────
$blocked = [ordered]@{
    WrittenAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    Entries      = @(
        # live blocks
        [ordered]@{ Ip = '203.0.113.17'; Failures = 12; BlockedAtUtc = $now.AddMinutes(-3).ToUniversalTime().ToString('o'); ExpiresAtUtc = $now.AddMinutes(7).ToUniversalTime().ToString('o') },
        [ordered]@{ Ip = '198.51.100.4'; Failures = 10; BlockedAtUtc = $now.AddMinutes(-8).ToUniversalTime().ToString('o'); ExpiresAtUtc = $now.AddMinutes(2).ToUniversalTime().ToString('o') },
        # already expired — must NOT appear in the UI
        [ordered]@{ Ip = '192.0.2.99'; Failures = 31; BlockedAtUtc = $now.AddHours(-2).ToUniversalTime().ToString('o'); ExpiresAtUtc = $now.AddHours(-1).ToUniversalTime().ToString('o') }
    )
}
$blocked | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Path 'data\blocked-ips.json') -Encoding UTF8
Write-Host '  blocked-ips : 2 live + 1 expired (the expired one must not show up)'

# ── metrics.db: email_events + smtp_session_stats for Activity and the top-N rankings ─────────
$buildDir = 'C:\Build\GraphMailer.NET\Debug'
$dll = Get-ChildItem (Join-Path $buildDir 'Microsoft.Data.Sqlite.dll') -ErrorAction SilentlyContinue
if (-not $dll) {
    Write-Host '  metrics.db  : SKIPPED — build the debug output first (.\build-debug.ps1)' -ForegroundColor Yellow
} else {
    # e_sqlite3.dll ships under runtimes\win-x64\native and is only found automatically by a
    # running .NET app, not by Add-Type — put it on PATH so the P/Invoke resolves.
    $native = Join-Path $buildDir 'runtimes\win-x64\native'
    if (Test-Path $native) { $env:PATH = "$native;$env:PATH" }

    Add-Type -Path (Join-Path $buildDir 'SQLitePCLRaw.core.dll')
    Add-Type -Path (Join-Path $buildDir 'SQLitePCLRaw.provider.e_sqlite3.dll')
    Add-Type -Path (Join-Path $buildDir 'SQLitePCLRaw.batteries_v2.dll')
    Add-Type -Path $dll.FullName
    [SQLitePCL.Batteries_V2]::Init()

    $dbPath = Join-Path $Path 'data\metrics.db'
    if (Test-Path $dbPath) { Remove-Item $dbPath -Force }

    $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$dbPath")
    $conn.Open()

    $schema = @'
CREATE TABLE email_events (
    id TEXT NOT NULL PRIMARY KEY, event_type TEXT NOT NULL, from_addr TEXT NOT NULL DEFAULT '',
    to_count INT NOT NULL DEFAULT 0, to_addrs TEXT, message_id TEXT NOT NULL DEFAULT '',
    subject TEXT, occurred_at TEXT NOT NULL, size_bytes INT NOT NULL DEFAULT 0,
    duration_ms INT NOT NULL DEFAULT 0, error_detail TEXT, client_ip TEXT,
    cc_count INT NOT NULL DEFAULT 0, bcc_count INT NOT NULL DEFAULT 0,
    attachment_count INT NOT NULL DEFAULT 0, attachment_bytes INT NOT NULL DEFAULT 0,
    listener_port INT NOT NULL DEFAULT 0, tls INT NOT NULL DEFAULT 0,
    authenticated INT NOT NULL DEFAULT 0, auth_user TEXT, retry_count INT NOT NULL DEFAULT 0,
    delivery_variant TEXT, queue_latency_ms INT NOT NULL DEFAULT 0, permanent INT NOT NULL DEFAULT 0);
CREATE INDEX idx_email_type_time ON email_events(event_type, occurred_at);
CREATE TABLE smtp_session_stats (
    bucket_hour TEXT NOT NULL, listener_port INT NOT NULL, client_ip TEXT NOT NULL,
    outcome TEXT NOT NULL, last_stage TEXT NOT NULL, tls INT NOT NULL, authenticated INT NOT NULL,
    count INT NOT NULL DEFAULT 0, total_duration_ms INT NOT NULL DEFAULT 0,
    UNIQUE(bucket_hour, listener_port, client_ip, outcome, last_stage, tls, authenticated));
CREATE INDEX idx_session_bucket ON smtp_session_stats(bucket_hour);
CREATE TABLE smtp_rejection_stats (
    bucket_hour TEXT NOT NULL, listener_port INT NOT NULL, client_ip TEXT NOT NULL,
    reason TEXT NOT NULL, count INT NOT NULL DEFAULT 0,
    UNIQUE(bucket_hour, listener_port, client_ip, reason));
CREATE INDEX idx_rejection_bucket ON smtp_rejection_stats(bucket_hour);
CREATE TABLE perf_metrics (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, metric_type TEXT NOT NULL,
    value REAL NOT NULL, recorded_at TEXT NOT NULL);
CREATE INDEX idx_perf_type_time ON perf_metrics(metric_type, recorded_at);
PRAGMA user_version = 2;
'@
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $schema; [void]$cmd.ExecuteNonQuery()

    $tx = $conn.BeginTransaction()

    # email_events — spread over 20 days so every time range shows a different amount
    $ins = $conn.CreateCommand()
    $ins.CommandText = @'
INSERT INTO email_events (id, event_type, from_addr, to_count, to_addrs, message_id, subject,
    occurred_at, size_bytes, duration_ms, error_detail, client_ip, attachment_count,
    listener_port, tls, authenticated, auth_user, retry_count, queue_latency_ms, permanent)
VALUES ($id,$t,$f,1,$to,$mid,$s,$at,$sz,$dur,$err,$ip,$att,$port,$tls,$auth,$user,$rc,$lat,$perm)
'@
    'id','t','f','to','mid','s','at','sz','dur','err','ip','att','port','tls','auth','user','rc','lat','perm' |
        ForEach-Object { [void]$ins.Parameters.Add($ins.CreateParameter()); $ins.Parameters[-1].ParameterName = "`$$_" }

    $types = 'received', 'sent', 'sent', 'failed'
    for ($i = 0; $i -lt $Events; $i++) {
        $t = $types[$i % $types.Count]
        $at = $now.AddMinutes(-$i * 19).ToUniversalTime().ToString('o')
        $isOldest = ($i -eq $Events - 1)

        $ins.Parameters['$id'].Value = [guid]::NewGuid().ToString()
        $ins.Parameters['$t'].Value = $t
        $ins.Parameters['$f'].Value = "app{0}@contoso.com" -f ($i % 12)
        $ins.Parameters['$to'].Value = "ops{0}@contoso.com" -f ($i % 25)
        $ins.Parameters['$mid'].Value = [guid]::NewGuid().ToString()
        $ins.Parameters['$s'].Value = if ($isOldest) { 'NEEDLE-OLDEST-EVENT' } else { "Backup Report {0:D5}" -f $i }
        $ins.Parameters['$at'].Value = $at
        $ins.Parameters['$sz'].Value = 2048 + ($i % 900000)
        $ins.Parameters['$dur'].Value = if ($t -eq 'sent') { 120 + ($i % 1500) } else { 0 }
        # More distinct error strings than the top-6 list can show
        $ins.Parameters['$err'].Value = if ($t -eq 'failed') { "GraphError{0:D2}: delivery rejected (RequestId {1})" -f ($i % $DistinctErrors), [guid]::NewGuid() } else { [DBNull]::Value }
        $ins.Parameters['$ip'].Value = '10.0.0.{0}' -f ($i % $DistinctHosts)
        $ins.Parameters['$att'].Value = $i % 3
        $ins.Parameters['$port'].Value = @(25, 587, 465)[$i % 3]
        $ins.Parameters['$tls'].Value = $i % 2
        $ins.Parameters['$auth'].Value = $i % 2
        $ins.Parameters['$user'].Value = "relay-user{0}" -f ($i % 5)
        $ins.Parameters['$rc'].Value = $i % 4
        $ins.Parameters['$lat'].Value = 200 + ($i % 5000)
        $ins.Parameters['$perm'].Value = if ($t -eq 'failed' -and $i % 2 -eq 0) { 1 } else { 0 }
        [void]$ins.ExecuteNonQuery()
    }

    # smtp_session_stats — more distinct client IPs than Top Client Hosts shows
    $sess = $conn.CreateCommand()
    $sess.CommandText = @'
INSERT INTO smtp_session_stats (bucket_hour, listener_port, client_ip, outcome, last_stage, tls, authenticated, count, total_duration_ms)
VALUES ($b,$p,$ip,$o,$st,$tls,$auth,$c,$d)
'@
    'b','p','ip','o','st','tls','auth','c','d' |
        ForEach-Object { [void]$sess.Parameters.Add($sess.CreateParameter()); $sess.Parameters[-1].ParameterName = "`$$_" }

    for ($h = 0; $h -lt 72; $h++) {
        for ($ip = 0; $ip -lt $DistinctHosts; $ip++) {
            foreach ($outcome in 'completed', 'aborted') {
                $sess.Parameters['$b'].Value = $now.AddHours(-$h).ToUniversalTime().ToString("yyyy-MM-dd'T'HH")
                $sess.Parameters['$p'].Value = @(25, 587, 465)[$ip % 3]
                $sess.Parameters['$ip'].Value = '10.0.0.{0}' -f $ip
                $sess.Parameters['$o'].Value = $outcome
                $sess.Parameters['$st'].Value = if ($outcome -eq 'aborted') { @('connect', 'ehlo', 'mail', 'data')[$ip % 4] } else { 'quit' }
                $sess.Parameters['$tls'].Value = $ip % 2
                $sess.Parameters['$auth'].Value = $ip % 2
                $sess.Parameters['$c'].Value = if ($outcome -eq 'aborted') { 1 + ($ip % 3) } else { 5 + ($ip % 9) }
                $sess.Parameters['$d'].Value = 500 * (1 + $ip)
                [void]$sess.ExecuteNonQuery()
            }
        }
    }

    $tx.Commit()
    $conn.Close()
    Write-Host "  metrics.db  : $Events events, $DistinctHosts hosts, $DistinctErrors error causes (marker NEEDLE-OLDEST-EVENT)"
}

Write-Host ''
Write-Host 'Done. Start the ConfigTool against this sandbox with:' -ForegroundColor Green
Write-Host ''
Write-Host "  `$env:GRAPHMAILER_DATA_DIR = '$Path'" -ForegroundColor White
Write-Host "  & 'C:\Build\GraphMailer.NET\Debug\GraphMailer.ConfigTool.exe'" -ForegroundColor White
Write-Host ''
Write-Host 'Remove it again with:  .\tools\seed-uitest-data.ps1 -Clean' -ForegroundColor DarkGray
