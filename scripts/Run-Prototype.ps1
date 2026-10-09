param([switch]$Build)
$ErrorActionPreference = 'Stop'
$smooveRoot = Split-Path -Parent $PSScriptRoot
$smooveDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $smooveDotnet)) { $smooveDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$smooveDll = Join-Path $smooveRoot 'src\Smoove.Probe\bin\Release\net10.0-windows\Smoove.Probe.dll'
if ($Build -or -not (Test-Path -LiteralPath $smooveDll)) {
    & $smooveDotnet build (Join-Path $smooveRoot 'Smoove.slnx') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}
Start-Process -FilePath $smooveDotnet -ArgumentList @(('"' + $smooveDll + '"')) -WorkingDirectory $smooveRoot -WindowStyle Hidden
