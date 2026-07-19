#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a Debug build of GraphMailer (Service + ConfigTool) into the shared
    flat output folder C:\Build\GraphMailer.NET\Debug\.

.DESCRIPTION
    - Passes --configuration Debug explicitly: the OutputPath redirect in
      src/Directory.Build.props is gated on $(Configuration) == 'Debug', which is
      still empty during props evaluation on a plain "dotnet build" — without the
      explicit flag the output silently lands in the in-repo bin\ folders.
    - Derives the build number identically to MSBuild (offset + git commit count,
      via tools/Get-BuildFileVersion.ps1) so the embedded FileVersion matches
      release builds from the same commit.
    - Fails early when GraphMailer.exe or the ConfigTool is still running —
      the running processes would lock the output files.
    - Builds the ConfigTool project; it references the Service, so one build
      compiles both into the same folder.

.PARAMETER Clean
    Wipe the Debug output folder and the Debug intermediates (bin\Debug,
    obj\Debug) before building.

.EXAMPLE
    .\build-debug.ps1
    .\build-debug.ps1 -Clean
#>
param(
    [switch] $Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Resolve paths ────────────────────────────────────────────────────────────
$root   = $PSScriptRoot
$svc    = Join-Path $root 'src\GraphMailer.Service\GraphMailer.Service.csproj'
$gui    = Join-Path $root 'src\GraphMailer.ConfigTool\GraphMailer.ConfigTool.csproj'
$outDir = 'C:\Build\GraphMailer.NET\Debug'

# ── Version — single source of truth (tools/Get-BuildFileVersion.ps1) ─────────
$ver         = & (Join-Path $root 'tools\Get-BuildFileVersion.ps1') -RepoRoot $root
$buildNumber = $ver.BuildNumber
$fileVersion = $ver.FileVersion

Write-Host ""
Write-Host "GraphMailer debug build" -ForegroundColor Cyan
Write-Host "  Version : $($ver.SemVer)  (file: $fileVersion)" -ForegroundColor Cyan
Write-Host "  Output  : $outDir" -ForegroundColor Cyan
Write-Host ""

# ── Guard: running processes lock the output files ───────────────────────────
$running = Get-Process -Name 'GraphMailer', 'GraphMailer.ConfigTool' -ErrorAction SilentlyContinue
if ($running) {
    $names = ($running | Select-Object -ExpandProperty ProcessName -Unique) -join ', '
    throw "Cannot build: $names is running and locks the output files. " +
          "Stop the GraphMailer service / close the ConfigTool first."
}

# ── Optional clean ───────────────────────────────────────────────────────────
if ($Clean) {
    Write-Host "Cleaning $outDir and Debug intermediates ..." -ForegroundColor Yellow
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    foreach ($proj in @($svc, $gui)) {
        $projDir = Split-Path $proj -Parent
        foreach ($sub in @('bin\Debug', 'obj\Debug')) {
            $dir = Join-Path $projDir $sub
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        }
    }
}

# ── Build: the ConfigTool references the Service, so this compiles both ──────
Write-Host "Building (Debug) ..." -ForegroundColor Green
dotnet build $gui `
    --configuration Debug `
    /p:_BuildNumber=$buildNumber `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

# ── Done ──────────────────────────────────────────────────────────────────────
$exes = Get-Item (Join-Path $outDir 'GraphMailer.exe'), (Join-Path $outDir 'GraphMailer.ConfigTool.exe')
Write-Host ""
Write-Host "Done." -ForegroundColor Cyan
foreach ($exe in $exes) {
    Write-Host ("  {0,-27} {1}  ({2:yyyy-MM-dd HH:mm:ss})" -f $exe.Name, $exe.VersionInfo.FileVersion, $exe.LastWriteTime) -ForegroundColor Cyan
}
Write-Host ""
