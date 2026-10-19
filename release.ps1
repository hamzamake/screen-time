# Publishes an update: builds dist\ScreenTime-Setup.exe for the given version and creates a GitHub release
# (tag v<Version>) with it attached. Every installed Screen Time checks that repo's latest release every few
# hours and installs it (or offers it, if the user turned automatic updates off).
#
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.5.0 -Notes "Fixed X, added Y"
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.5.0 -NotesFile notes.md
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.5.0 -Draft    (publish it yourself later)
#
# Needs the GitHub CLI (gh), logged in, and the repo named in ScreenTime\Updater.cs (Updater.Repo) to exist,
# be public, and have at least one commit. The version must be higher than the previous release's.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes,
    [string]$NotesFile,
    [switch]$Draft
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$Version = $Version.TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.5.0 (got '$Version')." }

$updater = [IO.File]::ReadAllText("$root\ScreenTime\Updater.cs")
if ($updater -notmatch 'const string Repo = "([^"]+)"') { throw "Couldn't find Updater.Repo in ScreenTime\Updater.cs." }
$repo = $Matches[1]

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "Install the GitHub CLI first: https://cli.github.com" }
$ErrorActionPreference = 'Continue'   # these checks fail on purpose sometimes; don't let stderr stop the script
gh repo view $repo --json name 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "GitHub repo '$repo' wasn't found. Create it (public, with a README so it has a commit) or change Updater.Repo, then rebuild."
}
$tag = "v$Version"
gh release view $tag --repo $repo 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists in $repo. Pick a higher version." }
$ErrorActionPreference = 'Stop'

$global:LASTEXITCODE = 0
& "$root\build.ps1" -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setup = "$root\dist\ScreenTime-Setup.exe"
$ghArgs = @('release', 'create', $tag, $setup, '--repo', $repo, '--title', "Screen Time $Version")
if ($NotesFile) { $ghArgs += @('--notes-file', $NotesFile) }
else { $ghArgs += @('--notes', $(if ($Notes) { $Notes } else { "Screen Time $Version" })) }
if ($Draft) { $ghArgs += '--draft' }

Write-Host "`nPublishing $tag to $repo..."
gh @ghArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "`nDone. Installed copies pick up $tag within a few hours (or right away via 'Check for updates')."
if ($Draft) { Write-Host "It's a draft: publish it on GitHub when you're ready (drafts aren't offered as updates)." }
