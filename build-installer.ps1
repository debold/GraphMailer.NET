#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the GraphMailer Windows installer: an MSI plus a setup.exe bootstrapper that
    chains the .NET 8 Desktop Runtime prerequisite.

.DESCRIPTION
    1. Publishes the framework-dependent release via build-release.ps1 (flat folder).
    2. Downloads the .NET 8 Desktop Runtime (x64) installer and resolves its immutable URL
       (so the bootstrapper can fetch it on demand and stays small).
    3. Builds GraphMailer-<ver>.msi (service registration + ConfigTool shortcut).
    4. Builds GraphMailerSetup-<ver>.exe (runtime prereq + MSI).

    Requires the WiX 5 tool:  dotnet tool install --global wix --version 5.*

.PARAMETER OutputRoot
    Where the installer artifacts are written. Default: C:\Build\GraphMailer.NET\Installers

.PARAMETER MsiOnly
    Build only the MSI (skip the runtime download and the bootstrapper).

.EXAMPLE
    .\build-installer.ps1

.NOTES
    Silent install / uninstall:
      GraphMailerSetup-<ver>.exe /quiet /norestart
      GraphMailerSetup-<ver>.exe /uninstall /quiet /norestart
      msiexec /i GraphMailer-<ver>.msi /qn /norestart
      msiexec /x GraphMailer-<ver>.msi /qn /norestart
#>
param(
    [string] $OutputRoot = 'C:\Build\GraphMailer.NET\Installers',
    [switch] $MsiOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This build renders the HTML help via build-help.ps1, which needs the modern .NET runtime
# (Markdig) and therefore PowerShell 7+. Keep the whole build in one edition: when started from
# Windows PowerShell 5.1 (PSEdition Desktop), relaunch the exact same invocation under pwsh 7 so
# the build never actually runs under 5.1. Original arguments are forwarded.
if ($PSVersionTable.PSEdition -ne 'Core') {
    $pwshExe = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
    if (-not $pwshExe) {
        throw "build-installer.ps1 requires PowerShell 7+ (pwsh), which was not found. " +
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

$root = $PSScriptRoot

# ── Require the WiX tool ──────────────────────────────────────────────────────
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "WiX 5 tool not found. Install it with: dotnet tool install --global wix --version 5.*"
}

# ── Version — single source of truth (tools/Get-BuildFileVersion.ps1) ─────────
$fileVer    = (& (Join-Path $root 'tools\Get-BuildFileVersion.ps1') -RepoRoot $root).FileVersion

$releaseDir = Join-Path 'C:\Build\GraphMailer.NET\Releases' $fileVer
$outDir     = Join-Path $OutputRoot $fileVer

Write-Host ""
Write-Host "GraphMailer installer build" -ForegroundColor Cyan
Write-Host "  Version : $fileVer" -ForegroundColor Cyan
Write-Host "  Output  : $outDir" -ForegroundColor Cyan
Write-Host ""

# ── 1. Publish the framework-dependent release ────────────────────────────────
Write-Host "Publishing release (framework-dependent) ..." -ForegroundColor Green
& (Join-Path $root 'build-release.ps1')
if ($LASTEXITCODE -ne 0) { throw "build-release.ps1 failed (exit $LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $releaseDir 'GraphMailer.exe'))) {
    throw "Expected publish output not found at $releaseDir"
}

# ── Prune non-distributable files before packaging ────────────────────────────
# Debug symbols (*.pdb) must not ship in the installer. Everything else in the
# publish is required at runtime (deps.json/runtimeconfig.json, appsettings.json,
# the flattened win-x64 native libs). build-release.ps1 keeps the symbols for
# manual deployments; only the installer payload is stripped here.
Write-Host "Pruning non-distributable files ..." -ForegroundColor Green
$pruned = @(Get-ChildItem $releaseDir -Recurse -File -Filter *.pdb)
foreach ($f in $pruned) {
    Write-Host "  remove $($f.Name)" -ForegroundColor DarkGray
    Remove-Item $f.FullName -Force
}
Write-Host "  $($pruned.Count) file(s) removed" -ForegroundColor DarkGray

# ── 1b. Build the user help into the publish folder so the MSI bundles it ──────
# A final installer always ships program + service + help. The help is generated
# straight into <release>\help, where Package.wxs harvests it with the rest of the
# publish (<Files Include="...\**">). It carries the same $fileVer as the binaries.
Write-Host "Building user help (HTML) ..." -ForegroundColor Green
& (Join-Path $root 'build-help.ps1') -OutputDir (Join-Path $releaseDir 'help') -Version $fileVer
if ($LASTEXITCODE -ne 0) { throw "build-help.ps1 failed (exit $LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $releaseDir 'help\index.html'))) {
    throw "Expected help output not found at $releaseDir\help"
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ── Resolve the WiX extension assemblies (added globally; referenced by path) ──
function Get-WixExt([string] $dllName) {
    $cache = Join-Path $env:USERPROFILE '.wix\extensions'
    $hit = Get-ChildItem $cache -Recurse -Filter $dllName -ErrorAction SilentlyContinue | Select-Object -First 1
    return $(if ($hit) { $hit.FullName } else { $null })
}
$utilExt = Get-WixExt 'WixToolset.Util.wixext.dll'
if (-not $utilExt) { wix extension add -g WixToolset.Util.wixext/5.0.2 | Out-Null; $utilExt = Get-WixExt 'WixToolset.Util.wixext.dll' }

# Firewall extension: the MSI registers an inbound firewall exception for the service exe.
$fwExt = Get-WixExt 'WixToolset.Firewall.wixext.dll'
if (-not $fwExt) { wix extension add -g WixToolset.Firewall.wixext/5.0.2 | Out-Null; $fwExt = Get-WixExt 'WixToolset.Firewall.wixext.dll' }

# ── Branding assets (generated by tools\generate-icons.ps1) ───────────────────
$iconFile = Join-Path $root 'installer\graphmailer.ico'
$logoFile = Join-Path $root 'src\GraphMailer.ConfigTool\Assets\graphmailer-base.png'

# ── 2. Build the MSI ──────────────────────────────────────────────────────────
$msi = Join-Path $outDir "GraphMailer-$fileVer.msi"
Write-Host "Building MSI ..." -ForegroundColor Green
wix build (Join-Path $root 'installer\Package.wxs') `
    -arch x64 `
    -d PublishDir="$releaseDir" `
    -d ProductVersion="$fileVer" `
    -d IconFile="$iconFile" `
    -ext "$fwExt" `
    -ext "$utilExt" `
    -o "$msi"
if ($LASTEXITCODE -ne 0) { throw "MSI build failed (exit $LASTEXITCODE)" }
Write-Host "  -> $msi" -ForegroundColor DarkGray

if ($MsiOnly) {
    Write-Host ""
    Write-Host "MSI-only build complete." -ForegroundColor Cyan
    return
}

# ── 3. Download the .NET 8 Desktop Runtime + resolve its immutable URL ─────────
Write-Host "Resolving .NET 8 Desktop Runtime (x64) ..." -ForegroundColor Green
$aka = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'

# Resolve the immutable download URL behind the aka.ms redirect. The response shape of
# Invoke-WebRequest differs between Windows PowerShell 5.1 (HttpWebResponse → ResponseUri)
# and PowerShell 7 (HttpResponseMessage → RequestMessage.RequestUri), so handle both.
$head  = Invoke-WebRequest -Uri $aka -Method Head -UseBasicParsing
$base  = $head.BaseResponse
$names = $base.PSObject.Properties.Name
$rtUrl =
    if ($names -contains 'ResponseUri')    { $base.ResponseUri.AbsoluteUri }            # Windows PowerShell 5.1
    elseif ($names -contains 'RequestMessage') { $base.RequestMessage.RequestUri.AbsoluteUri }  # PowerShell 7+
    else { throw "Could not resolve the runtime download URL from the redirect response." }

if ($rtUrl -notmatch 'windowsdesktop-runtime-(\d+\.\d+\.\d+)-win-x64\.exe') {
    throw "Could not parse runtime version from resolved URL: $rtUrl"
}
$rtVer = $Matches[1]

# Cache the installer between runs (keyed by version) to avoid re-downloading.
$rtCacheDir = Join-Path $env:TEMP 'gm-dotnet-runtime'
New-Item -ItemType Directory -Path $rtCacheDir -Force | Out-Null
$rtExe = Join-Path $rtCacheDir "windowsdesktop-runtime-$rtVer-win-x64.exe"
if (-not (Test-Path $rtExe)) {
    Write-Host "  downloading $rtVer ..." -ForegroundColor DarkGray
    Invoke-WebRequest -Uri $rtUrl -OutFile $rtExe -UseBasicParsing
}
Write-Host "  runtime $rtVer  ($([math]::Round((Get-Item $rtExe).Length/1MB,0)) MB)" -ForegroundColor DarkGray

# ── 4. Build the bootstrapper ─────────────────────────────────────────────────
$balExt = Get-WixExt 'WixToolset.BootstrapperApplications.wixext.dll'
if (-not $balExt) { wix extension add -g WixToolset.Bal.wixext/5.0.2 | Out-Null; $balExt = Get-WixExt 'WixToolset.BootstrapperApplications.wixext.dll' }

# Netfx extension: version-tolerant .NET runtime detection (netfx:DotNetCoreSearch in Bundle.wxs).
$netfxExt = Get-WixExt 'WixToolset.Netfx.wixext.dll'
if (-not $netfxExt) { wix extension add -g WixToolset.Netfx.wixext/5.0.2 | Out-Null; $netfxExt = Get-WixExt 'WixToolset.Netfx.wixext.dll' }

$setup = Join-Path $outDir "GraphMailerSetup-$fileVer.exe"
Write-Host "Building bootstrapper ..." -ForegroundColor Green
wix build (Join-Path $root 'installer\Bundle.wxs') `
    -arch x64 `
    -d ProductVersion="$fileVer" `
    -d MsiPath="$msi" `
    -d DotNetVersion="$rtVer" `
    -d DotNetRuntimeExe="$rtExe" `
    -d DotNetRuntimeUrl="$rtUrl" `
    -d IconFile="$iconFile" `
    -d LogoFile="$logoFile" `
    -ext "$utilExt" `
    -ext "$balExt" `
    -ext "$netfxExt" `
    -o "$setup"
if ($LASTEXITCODE -ne 0) { throw "Bootstrapper build failed (exit $LASTEXITCODE)" }

# ── Done ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Installer build complete:" -ForegroundColor Cyan
Write-Host "  MSI    : $msi" -ForegroundColor Cyan
Write-Host "  Setup  : $setup" -ForegroundColor Cyan
Write-Host ""
Write-Host "Silent install : GraphMailerSetup-$fileVer.exe /quiet /norestart" -ForegroundColor Gray
Write-Host "Silent remove  : GraphMailerSetup-$fileVer.exe /uninstall /quiet /norestart" -ForegroundColor Gray
Write-Host ""
