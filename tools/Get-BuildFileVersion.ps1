#Requires -Version 5.1
<#
.SYNOPSIS
    Single source of truth for the shipped GraphMailer FileVersion.

.DESCRIPTION
    FileVersion = <SemVer>.<BuildNumber> where
      SemVer      = <Version> from src/Directory.Build.props (maintained manually)
      BuildNumber = <_BuildNumberOffset> (from the same props) + <git commit count>

    The build number is a running counter: every commit bumps it by one, identical
    on every machine and reproducible from the git history alone. The offset keeps
    the 4th version field monotonically increasing across the 2026-07 switch from
    the old "days since 2026-01-01" scheme — it must stay above the highest revision
    ever shipped under that scheme (~199), so shipped versions never appear to go
    backwards (the update checker compares FileVersion as a four-part number).

    Returns a PSCustomObject with SemVer, BuildNumber and FileVersion so all build
    scripts derive the version identically instead of each recomputing it.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of this script's folder.
#>
param(
    [string] $RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$props = Join-Path $RepoRoot 'src\Directory.Build.props'
[xml]$xml = Get-Content $props -Raw

$semVer = $xml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if (-not $semVer) { throw "Could not read <Version> from $props" }

$offsetNode = $xml.SelectSingleNode('/Project/PropertyGroup/_BuildNumberOffset')
if (-not $offsetNode) { throw "Could not read <_BuildNumberOffset> from $props" }
$offset = [int]$offsetNode.InnerText

# git commit count; falls back to 0 (no history / git unavailable) so a build from
# an exported tree still produces a valid, if non-incrementing, FileVersion.
$commitCount = 0
try {
    $count = git -C $RepoRoot rev-list --count HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $count) { $commitCount = [int]$count.Trim() }
} catch {
    $commitCount = 0
}

$buildNumber = $offset + $commitCount

[pscustomobject]@{
    SemVer      = $semVer
    BuildNumber = $buildNumber
    FileVersion = "$semVer.$buildNumber"
}
