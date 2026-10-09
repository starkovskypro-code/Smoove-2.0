param([switch]$NativeInput)
$ErrorActionPreference = 'Stop'
$smooveRoot = Split-Path -Parent $PSScriptRoot
$smooveDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $smooveDotnet)) { $smooveDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $smooveDotnet build (Join-Path $smooveRoot 'Smoove.slnx') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $smooveDotnet run --project (Join-Path $smooveRoot 'tests\Smoove.Checks') -c Release --no-build -- (Join-Path $smooveRoot 'out\model-checks')
if ($LASTEXITCODE -ne 0) { throw 'Model checks failed' }
if ($NativeInput) {
    # Uses a dedicated test marker only accepted by integration mode inside its own foreground receiver.
    # Temporarily activates that window and positions/restores the cursor. Close the normal probe first.
    $smooveResult = Join-Path $smooveRoot 'out\native-input-check.txt'
    if (Test-Path -LiteralPath $smooveResult) { Remove-Item -LiteralPath $smooveResult }
    $smooveDll = Join-Path $smooveRoot 'src\Smoove.Probe\bin\Release\net10.0-windows\Smoove.Probe.dll'
    $smooveProcess = Start-Process -FilePath $smooveDotnet -ArgumentList @(('"' + $smooveDll + '"'), '--integration-check', ('"' + $smooveResult + '"')) -PassThru -WindowStyle Hidden
    if (-not $smooveProcess.WaitForExit(15000)) { $smooveProcess.Kill(); throw 'Native check timed out' }
    if (-not (Test-Path -LiteralPath $smooveResult)) { throw 'Native check produced no evidence' }
    $smooveEvidence = Get-Content -LiteralPath $smooveResult -Raw
    Write-Output $smooveEvidence
    if ($smooveProcess.ExitCode -ne 0 -or -not $smooveEvidence.StartsWith('PASS:')) { throw 'Native input check failed' }
}
