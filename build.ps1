# Builds the installer you can give to anyone: dist\ScreenTime-Setup.exe
#   - the app is published self-contained (the PC doesn't need .NET installed)
#   - the installer itself targets .NET Framework 4.8, which every Windows 10/11 PC already has
# Also writes dist\extension-for-store.zip (upload this to the Chrome Web Store / Edge Add-ons if you
# want browsers to install the extension automatically; see README).
# Needs the .NET 9 SDK.
#
# -Version: bump it for every release you publish (release.ps1 passes it). Installed copies update themselves
# to a GitHub release whose tag is a higher version, so the version built here must match the release tag.
# -Dev: instead, quickly rebuild dist\ScreenTime.exe + dist\extension for this PC (needs the .NET 9 runtime).
param([string]$Version = "1.4.0", [switch]$Dev)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.4.0 (got '$Version')." }

# The extension gets the app's version, so an unpacked copy can tell when an update replaced its files.
function Set-ExtensionVersion([string]$dir) {
    $m = Join-Path $dir 'manifest.json'
    $json = [IO.File]::ReadAllText($m) -replace '"version":\s*"[^"]*"', "`"version`": `"$Version`""
    [IO.File]::WriteAllText($m, $json)
}

if ($Dev) {
    dotnet publish "$root\ScreenTime\ScreenTime.csproj" -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:Version=$Version -o $dist --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Copy-Item -Recurse -Force "$root\extension\*" "$dist\extension\"
    Set-ExtensionVersion "$dist\extension"
    Write-Host "Built: $dist\ScreenTime.exe (dev)"
    exit 0
}

if (Test-Path $build) { Remove-Item -LiteralPath $build -Recurse -Force }
New-Item -ItemType Directory -Force $build, $dist | Out-Null

Write-Host "1/3  Publishing the app (self-contained)..."
dotnet publish "$root\ScreenTime\ScreenTime.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version -o "$build\app" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item -Recurse "$root\extension" "$build\app\extension"
Set-ExtensionVersion "$build\app\extension"
[IO.Compression.ZipFile]::CreateFromDirectory("$build\app", "$build\payload.zip", [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "2/3  Building the installer..."
dotnet build "$root\installer\Setup.csproj" -c Release -p:Version=$Version -p:PayloadZip="$build\payload.zip" -o "$build\setup" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item -Force "$build\setup\ScreenTime-Setup.exe" "$dist\ScreenTime-Setup.exe"

Write-Host "3/3  Packing the extension for the browser stores..."
$storeZip = "$dist\extension-for-store.zip"
if (Test-Path $storeZip) { Remove-Item -LiteralPath $storeZip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory("$build\app\extension", $storeZip, [IO.Compression.CompressionLevel]::Optimal, $false)

$size = [math]::Round((Get-Item "$dist\ScreenTime-Setup.exe").Length / 1MB, 1)
Write-Host "`nBuilt: $dist\ScreenTime-Setup.exe ($size MB), version $Version  - give this file to anyone."
