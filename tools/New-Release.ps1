#Requires -Version 5.1
<#
.SYNOPSIS
    Turns the current main into a release: dates the CHANGELOG section, commits
    "Release <version>" and tags it v<version>. Pushing the tag starts the release workflow.

.DESCRIPTION
    The release commit has to name its own build number (1000 + git commit count), which is the
    count *after* the commit exists. This script works it out instead of leaving it to mental
    arithmetic, and checks the result against tools/Get-BuildFileVersion.ps1 once the commit
    exists.

    Preconditions (the script stops if one is not met):
      - on main, working tree clean, not behind origin/main
      - CHANGELOG.md starts with a non-empty "## Unreleased" section
      - the SemVer in src/Directory.Build.props has not been released yet
      - -CoverageReviewed is given (release rule, docs/TEST_DOCUMENTATION.md -> Code coverage)

    Steps:
      1. "## Unreleased" becomes "## <version> - <date>" (em dash, like earlier releases)
      2. commit "Release <version>" with -Summary (or the section's bold headlines) as body
      3. lightweight tag v<version>
      4. with -Push: push main and the tag, which starts .github/workflows/release.yml

    After the release, bump <Version> in src/Directory.Build.props for the next development
    cycle ("Bump the version to x.y.z"), so builds stop carrying the released SemVer.

.PARAMETER Summary
    Body text of the release commit, a few sentences for operators. Defaults to the bold
    headlines of the CHANGELOG section.

.PARAMETER CoverageReviewed
    Confirms that .\tools\coverage.ps1 -IncludeLive was run and its gap list reviewed.

.PARAMETER Push
    Push main and the tag right away. Without it, the script prints the push commands.

.EXAMPLE
    .\tools\New-Release.ps1 -CoverageReviewed -Summary "A security maintenance release."
#>
param(
    [string] $Summary,
    [switch] $CoverageReviewed,
    [switch] $Push
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$changelog = Join-Path $root 'CHANGELOG.md'

function Invoke-Git {
    $output = & git -C $root @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed: $output" }
    return $output
}

# -- Preconditions -----------------------------------------------------------------------------

if (-not $CoverageReviewed) {
    throw 'Release rule: run .\tools\coverage.ps1 -IncludeLive, review the gap list (untested ' +
          'security or decision logic gets its tests first), then re-run with -CoverageReviewed.'
}

$branch = (Invoke-Git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { throw "Releases are made from main (current branch: $branch)." }

if (Invoke-Git status --porcelain) { throw 'The working tree has uncommitted changes. Commit or discard them first.' }

Invoke-Git fetch --quiet origin main | Out-Null
$behind = [int](Invoke-Git rev-list --count 'HEAD..origin/main')
if ($behind -gt 0) { throw "main is $behind commit(s) behind origin/main. Pull first." }

$lines = [IO.File]::ReadAllLines($changelog)
$firstHeading = ($lines | Select-String -Pattern '^## ' | Select-Object -First 1)
if (-not $firstHeading -or $firstHeading.Line.Trim() -ne '## Unreleased') {
    throw "CHANGELOG.md must start with a '## Unreleased' section (found: '$($firstHeading.Line)')."
}
$start = $firstHeading.LineNumber      # 1-based line of the heading = 0-based index of the next line
$next = $lines | Select-Object -Skip $start | Select-String -Pattern '^## ' | Select-Object -First 1
$sectionEnd = if ($next) { $start + $next.LineNumber - 1 } else { $lines.Count }
$section = @($lines[$start..($sectionEnd - 1)]) -join "`n"
if (-not $section.Trim()) { throw "The '## Unreleased' section in CHANGELOG.md is empty." }

$current = & (Join-Path $PSScriptRoot 'Get-BuildFileVersion.ps1') -RepoRoot $root
$version = '{0}.{1}' -f $current.SemVer, ($current.BuildNumber + 1)   # the release commit is the next one

$released = Invoke-Git tag --list "v$($current.SemVer).*"
if ($released) {
    throw "SemVer $($current.SemVer) is already released ($(@($released)[0])). Bump <Version> in src/Directory.Build.props first."
}
if (Invoke-Git ls-remote --tags origin "refs/tags/v$version") { throw "Tag v$version already exists on origin." }

# -- Release commit ----------------------------------------------------------------------------

$emDash = [char]0x2014
$date = Get-Date -Format 'yyyy-MM-dd'
$lines[$firstHeading.LineNumber - 1] = "## $version $emDash $date"
[IO.File]::WriteAllText($changelog, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

if (-not $Summary) {
    # Headlines may wrap onto the next line; collapse the line break and indentation.
    $headlines = [regex]::Matches($section, '(?ms)^- \*\*(.+?)\*\*') |
        ForEach-Object { '- ' + ($_.Groups[1].Value -replace '\s*\r?\n\s*', ' ') }
    $Summary = ($headlines -join "`n")
}

$message = "Release $version`n`n$Summary`n"
$messageFile = Join-Path ([IO.Path]::GetTempPath()) "gm-release-$version.txt"
[IO.File]::WriteAllText($messageFile, $message, [Text.UTF8Encoding]::new($false))

Invoke-Git add -- CHANGELOG.md | Out-Null
Invoke-Git commit --quiet --file $messageFile | Out-Null
Remove-Item $messageFile -ErrorAction SilentlyContinue

$built = (& (Join-Path $PSScriptRoot 'Get-BuildFileVersion.ps1') -RepoRoot $root).FileVersion
if ($built -ne $version) {
    Invoke-Git reset --soft HEAD~1 | Out-Null
    throw "The release commit builds $built, not the predicted $version. The commit was undone; investigate before retrying."
}

Invoke-Git tag "v$version" | Out-Null

Write-Host ''
Write-Host "Release $version committed and tagged v$version." -ForegroundColor Green

if ($Push) {
    Invoke-Git push --quiet origin main | Out-Null
    Invoke-Git push --quiet origin "v$version" | Out-Null
    Write-Host 'Pushed. The release workflow builds, tests the installer and creates a draft release:' -ForegroundColor Green
    Write-Host '  https://github.com/debold/GraphMailer.NET/actions/workflows/release.yml'
} else {
    Write-Host 'Next: push both. The tag starts the release workflow (build, installer tests, draft release).'
    Write-Host '  git push origin main'
    Write-Host "  git push origin v$version"
}
Write-Host "Afterwards: bump <Version> in src/Directory.Build.props for the next cycle."
