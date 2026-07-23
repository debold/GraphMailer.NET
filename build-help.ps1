<#
.SYNOPSIS
    Builds the GraphMailer user help: converts docs/help/**.md to standalone HTML files
    wrapped in docs/help/_template.html, with a shared sidebar, breadcrumb and prev/next nav.

.DESCRIPTION
    Markdown is the single source; HTML is generated. Rendering uses Markdig (downloaded from
    nuget.org on first run and cached under %TEMP%\gm-markdig, mirroring how build-installer.ps1
    caches the .NET runtime). GitHub-style callouts (> [!NOTE] / [!TIP] / [!IMPORTANT] /
    [!WARNING] / [!CAUTION]) are rendered via Markdig's alert-block extension and styled by
    assets/help.css.

    The page order, titles and sidebar grouping live in the $SiteMap below — the single place
    to register a new help page. Pages whose .md file does not exist yet are skipped with a
    warning, so the build works while the documentation is still being written.

    The help does not carry its own version: the footer shows the same version as the EXEs
    (FileVersion via tools/Get-BuildFileVersion.ps1 = SemVer + offset + git commit count). Pass
    -Version to override (build-installer.ps1 passes the exact build version it stamps).

.PARAMETER OutputDir
    Where to write the HTML tree. Defaults to docs/help/_site (git-ignored). build-installer.ps1
    points this at the publish folder's help\ subdirectory so the MSI bundles it.

.PARAMETER Version
    Version string shown in the footer. Defaults to the same value the release/installer build
    derives, so the help always matches the shipped binaries.

.EXAMPLE
    .\build-help.ps1
.EXAMPLE
    .\build-help.ps1 -OutputDir C:\Build\GraphMailer.NET\Releases\1.1.0.167\help -Version 1.1.0.167
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$helpRoot = Join-Path $repoRoot 'docs\help'
if (-not $OutputDir) { $OutputDir = Join-Path $helpRoot '_site' }

# Markdig requires the modern .NET runtime (PowerShell 7+). Windows PowerShell 5.1 (.NET
# Framework, PSEdition Desktop) cannot load it — it pulls in System.Memory, absent from the
# .NET Framework GAC. When started from 5.1, relaunch the exact same invocation under pwsh 7
# (forwarding the original arguments) so the rendering never runs under 5.1.
if ($PSVersionTable.PSEdition -ne 'Core') {
    $pwshExe = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
    if (-not $pwshExe) {
        throw "build-help.ps1 requires PowerShell 7+ (pwsh), which was not found. " +
              "Install PowerShell 7, or run it explicitly: pwsh -File `"$PSCommandPath`""
    }
    $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    foreach ($p in $PSBoundParameters.GetEnumerator()) {
        if ($p.Value -is [System.Management.Automation.SwitchParameter]) {
            if ($p.Value.IsPresent) { $forward += "-$($p.Key)" }
        } else {
            $forward += "-$($p.Key)"; $forward += [string]$p.Value
        }
    }
    & $pwshExe @forward
    exit $LASTEXITCODE
}

# ── Site map: the authoritative page order + grouping ────────────────────────
# Path is relative to docs/help and uses .md; output mirrors it with .html.
$SiteMap = @(
    @{ Group = '';                Path = 'index.md';                          Title = 'Home' }
    @{ Group = 'Getting Started'; Path = 'getting-started/installation.md';   Title = 'Installation' }
    @{ Group = 'Getting Started'; Path = 'getting-started/quickstart.md';     Title = 'Quickstart' }
    @{ Group = 'Getting Started'; Path = 'getting-started/entra-setup.md';    Title = 'Entra / Graph Setup' }
    @{ Group = 'Configuration';   Path = 'configuration/servers-tls.md';      Title = 'Servers & TLS' }
    @{ Group = 'Configuration';   Path = 'configuration/access-control.md';   Title = 'Access Control' }
    @{ Group = 'Configuration';   Path = 'configuration/ip-filtering.md';     Title = 'IP Filtering' }
    @{ Group = 'Configuration';   Path = 'configuration/graph-api.md';        Title = 'Graph API' }
    @{ Group = 'Configuration';   Path = 'configuration/mail-queue.md';       Title = 'Mail Queue' }
    @{ Group = 'Configuration';   Path = 'configuration/monitoring.md';       Title = 'Monitoring' }
    @{ Group = 'Configuration';   Path = 'configuration/notifications.md';    Title = 'Notifications' }
    @{ Group = 'Configuration';   Path = 'configuration/backup-restore.md';   Title = 'Backup & Restore' }
    @{ Group = 'Monitoring';      Path = 'monitoring/status.md';              Title = 'Status' }
    @{ Group = 'Monitoring';      Path = 'monitoring/recommendations.md';     Title = 'Recommendations' }
    @{ Group = 'Monitoring';      Path = 'monitoring/metrics.md';             Title = 'Metrics' }
    @{ Group = 'Monitoring';      Path = 'monitoring/messages.md';            Title = 'Messages' }
    @{ Group = 'Monitoring';      Path = 'monitoring/logs.md';                Title = 'Logs' }
    @{ Group = 'Reference';       Path = 'reference/troubleshooting.md';      Title = 'Troubleshooting' }
    @{ Group = 'Reference';       Path = 'reference/faq.md';                  Title = 'FAQ' }
    @{ Group = 'Reference';       Path = 'reference/glossary.md';             Title = 'Glossary' }
)

# ── Resolve Markdig (download + cache the dependency-free DLL) ────────────────
function Get-Markdig {
    $mdVersion = '0.38.0'
    $cache = Join-Path $env:TEMP "gm-markdig\$mdVersion"

    # Load the build that matches the host runtime: build-installer.ps1 runs under
    # Windows PowerShell 5.1 (.NET Framework, PSEdition Desktop) — there the net8.0
    # assembly throws ReflectionTypeLoadException, so use netstandard2.0. PowerShell 7+
    # (PSEdition Core, .NET) takes the net8.0 build.
    $tfms = if ($PSVersionTable.PSEdition -eq 'Core') {
        @('net8.0', 'netstandard2.1', 'netstandard2.0')
    } else {
        @('netstandard2.0', 'net462')
    }

    $resolve = {
        foreach ($tfm in $tfms) {
            $p = Join-Path $cache "lib\$tfm\Markdig.dll"
            if (Test-Path $p) { return $p }
        }
        return $null
    }

    $dll = & $resolve
    if (-not $dll) {
        Write-Host "Downloading Markdig $mdVersion ..." -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $cache | Out-Null
        $zip = Join-Path $cache 'markdig.zip'
        Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Markdig/$mdVersion" -OutFile $zip
        Expand-Archive -Path $zip -DestinationPath $cache -Force
        Remove-Item $zip -Force
        $dll = & $resolve
    }
    if (-not $dll) {
        throw "Could not resolve a Markdig.dll for PowerShell $($PSVersionTable.PSEdition) under $cache"
    }
    Add-Type -Path $dll
}

# Version: reuse the caller-supplied value, otherwise derive it exactly like
# build-release.ps1 / build-installer.ps1 so the help matches the shipped EXEs.
function Resolve-Version {
    if ($Version) { return $Version }
    $helper = Join-Path $repoRoot 'tools\Get-BuildFileVersion.ps1'
    if (Test-Path $helper) {
        try { return (& $helper -RepoRoot $repoRoot).FileVersion } catch { return 'dev' }
    }
    return 'dev'
}

Get-Markdig
$pipeline = [Markdig.MarkdownPipelineBuilder]::new()
$pipeline = [Markdig.MarkdownExtensions]::UseAdvancedExtensions($pipeline)
$pipeline = [Markdig.MarkdownExtensions]::UseAlertBlocks($pipeline)
$pipeline = $pipeline.Build()

$template = Get-Content -Raw (Join-Path $helpRoot '_template.html')
$version = Resolve-Version
$generated = (Get-Date).ToString('yyyy-MM-dd')

# Pre-compute, for each page, the html href relative to docs/help root.
foreach ($p in $SiteMap) { $p.Html = [IO.Path]::ChangeExtension($p.Path, '.html') -replace '\\', '/' }

# Only pages that actually exist get built / linked.
$present = $SiteMap | Where-Object { Test-Path (Join-Path $helpRoot $_.Path) }
$built = 0

function Get-RootPrefix([string]$htmlPath) {
    $depth = ($htmlPath -split '/').Count - 1
    if ($depth -le 0) { return '' }
    return ('../' * $depth)
}

function Build-Sidebar([string]$rootPrefix, [string]$currentHtml) {
    $sb = [System.Text.StringBuilder]::new()
    $lastGroup = $null
    foreach ($p in $present) {
        if ($p.Group -ne $lastGroup -and $p.Group -ne '') {
            [void]$sb.AppendLine("    <div class=`"group-label`">$($p.Group)</div>")
            $lastGroup = $p.Group
        }
        $cls = if ($p.Html -eq $currentHtml) { ' class="active"' } else { '' }
        [void]$sb.AppendLine("    <a href=`"$rootPrefix$($p.Html)`"$cls>$($p.Title)</a>")
    }
    return $sb.ToString()
}

function Build-Breadcrumb([string]$rootPrefix, $page) {
    $homeLink = "<a href=`"${rootPrefix}index.html`">Home</a>"
    if ($page.Html -eq 'index.html') { return 'Home' }
    if ($page.Group) { return "$homeLink &rsaquo; $($page.Group) &rsaquo; $($page.Title)" }
    return "$homeLink &rsaquo; $($page.Title)"
}

function Build-PrevNext([string]$rootPrefix, [int]$index) {
    $prev = if ($index -gt 0) { $present[$index - 1] } else { $null }
    $next = if ($index -lt $present.Count - 1) { $present[$index + 1] } else { $null }
    if (-not $prev -and -not $next) { return '' }
    $html = '<nav class="prevnext">'
    if ($prev) {
        $html += "<a class=`"prev`" href=`"$rootPrefix$($prev.Html)`"><span class=`"dir`">&larr; Previous</span><span class=`"ttl`">$($prev.Title)</span></a>"
    } else { $html += '<span></span>' }
    if ($next) {
        $html += "<a class=`"next`" href=`"$rootPrefix$($next.Html)`"><span class=`"dir`">Next &rarr;</span><span class=`"ttl`">$($next.Title)</span></a>"
    } else { $html += '<span></span>' }
    $html += '</nav>'
    return $html
}

for ($i = 0; $i -lt $present.Count; $i++) {
    $page = $present[$i]
    $md = Get-Content -Raw (Join-Path $helpRoot $page.Path)
    $bodyHtml = [Markdig.Markdown]::ToHtml($md, $pipeline)

    $rootPrefix = Get-RootPrefix $page.Html
    $out = $template
    $out = $out.Replace('{{ROOT}}', $rootPrefix)
    $out = $out.Replace('{{TITLE}}', $page.Title)
    $out = $out.Replace('{{SIDEBAR}}', (Build-Sidebar $rootPrefix $page.Html))
    $out = $out.Replace('{{BREADCRUMB}}', (Build-Breadcrumb $rootPrefix $page))
    $out = $out.Replace('{{CONTENT}}', $bodyHtml)
    $out = $out.Replace('{{PREVNEXT}}', (Build-PrevNext $rootPrefix $i))
    $out = $out.Replace('{{VERSION}}', $version)
    $out = $out.Replace('{{GENERATED}}', $generated)

    $dest = Join-Path $OutputDir ($page.Html -replace '/', '\')
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    Set-Content -Path $dest -Value $out -Encoding UTF8
    $built++
}

# Copy assets alongside the generated pages.
Copy-Item -Path (Join-Path $helpRoot 'assets') -Destination (Join-Path $OutputDir 'assets') -Recurse -Force

$skipped = $SiteMap.Count - $present.Count
Write-Host "Built $built help page(s) (v$version) -> $OutputDir" -ForegroundColor Green
if ($skipped -gt 0) { Write-Host "Skipped $skipped not-yet-written page(s)." -ForegroundColor DarkYellow }
