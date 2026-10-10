param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version' }
$smooveRoot = Split-Path -Parent $PSScriptRoot
$smooveBranch = (& git -C $smooveRoot branch --show-current).Trim()
if ($smooveBranch -ne 'main') { throw "Build release artifacts from merged main; current branch is $smooveBranch" }
& git -C $smooveRoot diff --quiet HEAD --
if ($LASTEXITCODE -ne 0) { throw 'Tracked source changes exist; build the release from committed main.' }
& git -C $smooveRoot ls-files --error-unmatch src/Smoove.Settings/Smoove.Settings.csproj installer/Smoove.iss scripts/Build-Installer.ps1 "docs/RELEASE-v$Version.md" 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Release inputs are not tracked at this commit.' }
$smooveNotes = Join-Path $smooveRoot "docs\RELEASE-v$Version.md"
if (!(Test-Path -LiteralPath $smooveNotes)) { throw "Release notes are missing: $smooveNotes" }
& (Join-Path $PSScriptRoot 'Build-Installer.ps1') -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
$smooveOutput = Join-Path $smooveRoot ('out\installer\' + $Version)
$smooveInstaller = Join-Path $smooveOutput "Smoove-$Version-Setup-win-x64.exe"
if (!(Test-Path -LiteralPath $smooveInstaller)) { throw 'Installer asset missing' }
Get-Content -LiteralPath (Join-Path $smooveOutput 'SHA256SUMS.txt')
Write-Output ('Installer ready for GitHub Release upload: ' + $smooveInstaller)
