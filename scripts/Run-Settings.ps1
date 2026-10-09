$ErrorActionPreference = 'Stop'
$smooveRoot = Split-Path -Parent $PSScriptRoot
$smooveExe = Join-Path $smooveRoot 'out\app\Smoove.Settings.exe'
if (!(Test-Path -LiteralPath $smooveExe)) {
    $smooveDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (!(Test-Path -LiteralPath $smooveDotnet)) { $smooveDotnet = (Get-Command dotnet).Source }
    & $smooveDotnet publish (Join-Path $smooveRoot 'src\Smoove.Settings\Smoove.Settings.csproj') -c Release -p:Platform=x64 -p:PublishTrimmed=false --self-contained true -o (Join-Path $smooveRoot 'out\app')
    if ($LASTEXITCODE -ne 0) { throw 'Settings publish failed' }
}
Start-Process -FilePath $smooveExe -WorkingDirectory $smooveRoot -WindowStyle Hidden
