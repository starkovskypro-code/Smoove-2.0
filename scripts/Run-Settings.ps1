$ErrorActionPreference = 'Stop'
$smooveRoot = Split-Path -Parent $PSScriptRoot
$smooveExe = Join-Path $smooveRoot 'out/app/Smoove.Settings.exe'
if (!(Test-Path -LiteralPath $smooveExe)) { throw 'Current local build is missing: out/app/Smoove.Settings.exe. Never launch an installed copy.' }
$manifestPath = Join-Path $smooveRoot 'out/app/local-build.json'
if (!(Test-Path -LiteralPath $manifestPath)) { throw 'Local build manifest is missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($file in $manifest.Files) {
    $path = Join-Path $smooveRoot $file.Path
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Hash) { throw ('Local build is stale or changed: ' + $file.Path + '. Refresh the local build explicitly before launch.') }
}
$existing = Get-Process -Name Smoove.Settings -ErrorAction SilentlyContinue
foreach ($process in $existing) {
    if ($process.Path -ne $smooveExe) { throw ('Another Smoove copy is running: ' + $process.Path + '. Close it before launching the local build.') }
}
if (!$existing) { Start-Process -FilePath $smooveExe -WorkingDirectory $smooveRoot }
