#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a production release of GraphMailer (Service + ConfigTool).

.DESCRIPTION
    - Reads the semantic version from src/Directory.Build.props
    - Derives the build number identically to MSBuild (days since 2026-01-01 UTC)
    - Publishes to C:\Build\GraphMailer.NET\Releases\<version>\
    - Cleans the target directory before building
    - Service is published first; ConfigTool second (it is a superset of dependencies)

.PARAMETER OutputRoot
    Override the root output path. Default: C:\Build\GraphMailer.NET\Releases

.EXAMPLE
    .\build-release.ps1
    .\build-release.ps1 -OutputRoot D:\Deployments\GraphMailer
#>
param(
    [string] $OutputRoot = 'C:\Build\GraphMailer.NET\Releases',
    # Bundle the .NET runtime into the output (no .NET needed on the target,
    # ~260 MB). Default: framework-dependent — requires the .NET Desktop
    # Runtime 8 (x64) on the target machine, but is a fraction of the size.
    [switch] $SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Resolve paths ────────────────────────────────────────────────────────────
$root   = $PSScriptRoot
$props  = Join-Path $root 'src\Directory.Build.props'
$svc    = Join-Path $root 'src\GraphMailer.Service\GraphMailer.Service.csproj'
$gui    = Join-Path $root 'src\GraphMailer.ConfigTool\GraphMailer.ConfigTool.csproj'

# ── Read semantic version from Directory.Build.props ─────────────────────────
[xml]$xml    = Get-Content $props -Raw
$versionNode = $xml.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode) { throw "Could not read <Version> from $props" }
$semVer      = $versionNode.InnerText

# ── Compute build number once; passed to MSBuild via /p:_BuildNumber so the
#    folder name and the FileVersion baked into both binaries always match,
#    even when the build spans UTC midnight. ──────────────────────────────────
$epoch       = [datetime]::new(2026, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
$buildNumber = ([datetime]::UtcNow - $epoch).Days
$fileVersion = "$semVer.$buildNumber"
$infoVersion = "$semVer+$([datetime]::UtcNow.ToString('yyyyMMdd'))"
$bundleFlag  = if ($SelfContained) { 'true' } else { 'false' }

# ── Target directory ─────────────────────────────────────────────────────────
$outDir = Join-Path $OutputRoot $fileVersion

Write-Host ""
Write-Host "GraphMailer release build" -ForegroundColor Cyan
Write-Host "  Version   : $semVer  (file: $fileVersion)" -ForegroundColor Cyan
Write-Host "  Output    : $outDir" -ForegroundColor Cyan
Write-Host ("  Runtime   : " + ($(if ($SelfContained) { 'self-contained (bundled .NET)' } else { 'framework-dependent (.NET Desktop Runtime 8 x64 required on target)' }))) -ForegroundColor Cyan
Write-Host ""

# ── Clean ────────────────────────────────────────────────────────────────────
if (Test-Path $outDir) {
    Write-Host "Cleaning $outDir ..." -ForegroundColor Yellow
    Remove-Item $outDir -Recurse -Force
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# Releases must build from fresh intermediates: the incremental ReadyToRun step
# does not invalidate its cached images (obj\Release\R2R\) when a NuGet package
# version changes, and the --no-build publishes below would then ship a stale
# DLL that no longer matches the freshly generated deps.json (seen once with
# Microsoft.Data.Sqlite 8.0.28 vs 8.0.29 → FileNotFoundException at runtime).
Write-Host "Cleaning Release intermediates (bin\Release, obj\Release) ..." -ForegroundColor Yellow
foreach ($proj in @($svc, $gui)) {
    $projDir = Split-Path $proj -Parent
    foreach ($sub in @('bin\Release', 'obj\Release')) {
        $dir = Join-Path $projDir $sub
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
}

# ── Restore ──────────────────────────────────────────────────────────────────
Write-Host "Restoring dependencies ..." -ForegroundColor DarkGray
dotnet restore (Join-Path $root 'GraphMailer.sln') --runtime win-x64 /p:PublishReadyToRun=true --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)" }

# ── Build once: the ConfigTool references the Service, so this compiles both ──
Write-Host ""
Write-Host "Building (Release) ..." -ForegroundColor Green
dotnet build $gui `
    --configuration Release `
    --no-restore `
    /p:_BuildNumber=$buildNumber `
    /p:InformationalVersion=$infoVersion `
    /p:SelfContained=$bundleFlag `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

# ── Publish Service ───────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Publishing GraphMailer.Service ..." -ForegroundColor Green
dotnet publish $svc `
    --configuration Release `
    --no-build `
    /p:PublishDir="$outDir\\" `
    /p:_BuildNumber=$buildNumber `
    /p:InformationalVersion=$infoVersion `
    /p:SelfContained=$bundleFlag `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Service publish failed (exit $LASTEXITCODE)" }

# ── Publish ConfigTool ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Publishing GraphMailer.ConfigTool ..." -ForegroundColor Green
dotnet publish $gui `
    --configuration Release `
    --no-build `
    /p:PublishDir="$outDir\\" `
    /p:_BuildNumber=$buildNumber `
    /p:InformationalVersion=$infoVersion `
    /p:SelfContained=$bundleFlag `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "ConfigTool publish failed (exit $LASTEXITCODE)" }

# ── Done ──────────────────────────────────────────────────────────────────────
$fileCount = (Get-ChildItem $outDir -File).Count
Write-Host ""
Write-Host "Done. $fileCount files in $outDir" -ForegroundColor Cyan
Write-Host ""
