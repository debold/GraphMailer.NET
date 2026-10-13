#Requires -Version 5.1
<#
.SYNOPSIS
    Measures code coverage of the test suites and lists the biggest gaps.

.DESCRIPTION
    Runs the unit and integration suites (optionally the live suite) with the coverlet
    data collector, merges the per-suite Cobertura results and prints:

      - line and branch coverage per assembly
      - line coverage per folder
      - the files with the most uncovered lines (the gap list to review)

    A line counts as covered when any suite executed it. Each suite reports paths relative
    to its own source root, so they are normalised before merging. Source-generated code
    (obj\) is excluded.

    Coverage is a diagnostic for finding untested code, not a quality score - there is
    deliberately no threshold that fails the run. Review the gap list for untested
    security and decision logic before a release (see the release checklist).

    Results go outside the repository (OneDrive-synced): the raw Cobertura XML and a copy
    of the printed summary (summary.txt) land in -OutputDir, which is cleared on each run.

.PARAMETER OutputDir
    Where the Cobertura files and summary.txt are written. Default:
    C:\Build\GraphMailer.NET\Coverage

.PARAMETER IncludeLive
    Also run tests/GraphMailer.Tests.Live. It sends real mail in the test tenant and
    self-skips without credentials. Without it the Graph path (GraphApiClient,
    GraphDirectoryGateway, GraphClientProvider) reads lower than it really is.

.PARAMETER Top
    Number of files in the gap list. Default 25.

.PARAMETER IncludeUi
    Also list WPF code-behind (*.xaml.cs, *.xaml) in the gap list. Off by default: that code is not
    realistically unit-testable and would crowd out the actionable gaps. It is always part
    of the per-assembly and per-folder figures.

.EXAMPLE
    .\tools\coverage.ps1
    .\tools\coverage.ps1 -IncludeLive
#>
param(
    [string] $OutputDir = 'C:\Build\GraphMailer.NET\Coverage',
    [switch] $IncludeLive,
    [int]    $Top = 25,
    [switch] $IncludeUi
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent

# -- Run the suites with coverage ----------------------------------------------
$suites = @('Unit', 'Integration')
if ($IncludeLive) { $suites += 'Live' }

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

foreach ($suite in $suites) {
    Write-Host "Running $suite tests with coverage ..." -ForegroundColor Cyan
    $project = Join-Path $repoRoot "tests\GraphMailer.Tests.$suite"
    $output = & dotnet test $project -c Debug --collect:'XPlat Code Coverage' `
        --results-directory (Join-Path $OutputDir $suite) 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | Select-Object -Last 30 | Write-Host
        throw "$suite tests failed (exit $LASTEXITCODE) - fix the suite before measuring coverage."
    }
    $output | Where-Object { $_ -match '(Passed|Bestanden)!' } | Write-Host -ForegroundColor DarkGray
}

# -- Merge the Cobertura results -----------------------------------------------
$lineHits = @{}   # "<assembly>|<file>|<line>" -> covered by any suite
$branchHits = @{} # same key -> @(covered, total), best result across suites

foreach ($xmlFile in Get-ChildItem $OutputDir -Recurse -Filter coverage.cobertura.xml) {
    [xml] $xml = Get-Content $xmlFile.FullName -Raw
    $sourceRoot = @($xml.coverage.sources.source)[0]
    foreach ($package in $xml.coverage.packages.package) {
        foreach ($class in $package.classes.class) {
            $path = $class.filename
            if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $sourceRoot $path }
            $file = ($path -replace '/', '\') -replace '^.*\\src\\', ''
            if ($file -match '\\obj\\') { continue }

            foreach ($line in $class.lines.line) {
                $key = "$($package.name)|$file|$($line.number)"
                $lineHits[$key] = ($lineHits[$key] -eq $true) -or ([int]$line.hits -gt 0)
                if ($line.branch -eq 'True' -and $line.'condition-coverage' -match '\((\d+)/(\d+)\)') {
                    $covered = [int]$Matches[1]
                    $previous = $branchHits[$key]
                    if (-not $previous -or $covered -gt $previous[0]) { $branchHits[$key] = @($covered, [int]$Matches[2]) }
                }
            }
        }
    }
}

if ($lineHits.Count -eq 0) { throw "No coverage data found under $OutputDir." }

function Format-Percent([double] $part, [double] $whole) {
    if ($whole -eq 0) { return '    -' }
    return '{0,5:N1} %' -f (100.0 * $part / $whole)
}

$rows = foreach ($key in $lineHits.Keys) {
    $parts = $key.Split('|')
    [pscustomobject]@{ Assembly = $parts[0]; File = $parts[1]; Covered = [int]$lineHits[$key] }
}

# -- Report --------------------------------------------------------------------
$report = New-Object System.Collections.Generic.List[string]
$report.Add("GraphMailer code coverage - suites: $($suites -join ', ') - $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
if (-not $IncludeLive) {
    $report.Add('(Live suite not included: the Graph path reads lower than it is. Use -IncludeLive.)')
}
$report.Add('')

$report.Add('== Coverage per assembly')
foreach ($group in $rows | Group-Object Assembly | Sort-Object Name) {
    $covered = ($group.Group | Measure-Object Covered -Sum).Sum
    $branchCovered = 0; $branchTotal = 0
    foreach ($key in $branchHits.Keys) {
        if ($key.StartsWith("$($group.Name)|")) { $branchCovered += $branchHits[$key][0]; $branchTotal += $branchHits[$key][1] }
    }
    $report.Add(('{0,-24} lines {1}  ({2}/{3})   branches {4}' -f $group.Name,
        (Format-Percent $covered $group.Count), $covered, $group.Count, (Format-Percent $branchCovered $branchTotal)))
}
$report.Add('')

$report.Add('== Line coverage per folder')
$folderRows = $rows | ForEach-Object {
    [pscustomobject]@{ Folder = (Split-Path $_.File -Parent); Covered = $_.Covered }
}
foreach ($group in $folderRows | Group-Object Folder | Sort-Object Name) {
    $covered = ($group.Group | Measure-Object Covered -Sum).Sum
    $report.Add(('{0,-52} {1}  ({2,5} lines)' -f $group.Name, (Format-Percent $covered $group.Count), $group.Count))
}
$report.Add('')

$scope = if ($IncludeUi) { '' } else { ' (WPF code-behind excluded; -IncludeUi lists it)' }
$report.Add("== Top $Top files by uncovered lines$scope")
$fileRows = foreach ($group in $rows | Group-Object File) {
    # *.xaml entries come from the markup compiler's #line mapping of generated code
    if (-not $IncludeUi -and ($group.Name -like '*.xaml.cs' -or $group.Name -like '*.xaml')) { continue }
    $covered = [int]($group.Group | Measure-Object Covered -Sum).Sum
    [pscustomobject]@{ File = $group.Name; Uncovered = $group.Count - $covered; Lines = $group.Count; Coverage = (Format-Percent $covered $group.Count) }
}
$fileRows | Sort-Object Uncovered -Descending | Select-Object -First $Top |
    Format-Table -AutoSize | Out-String -Width 200 | ForEach-Object { $report.Add($_.TrimEnd()) }

$report | ForEach-Object { Write-Host $_ }
$summary = Join-Path $OutputDir 'summary.txt'
[IO.File]::WriteAllLines($summary, $report, [Text.UTF8Encoding]::new($false))
Write-Host ''
Write-Host "Summary and Cobertura files: $OutputDir" -ForegroundColor Green
