param([switch]$NativeInput, [switch]$YandexBrowser, [switch]$TelegramChat, [switch]$ExplorerFiles, [switch]$Published)
$ErrorActionPreference = 'Stop'
$smooveRoot = Split-Path -Parent $PSScriptRoot
$smooveDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $smooveDotnet)) { $smooveDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $smooveDotnet build (Join-Path $smooveRoot 'Smoove.slnx') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $smooveDotnet run --project (Join-Path $smooveRoot 'tests\Smoove.Checks') -c Release --no-build -- (Join-Path $smooveRoot 'out\model-checks')
if ($LASTEXITCODE -ne 0) { throw 'Model checks failed' }
function Invoke-SmooveInputCheck([string]$Mode, [string]$ReportName) {
    # Uses a dedicated test marker only accepted by integration mode inside its own foreground receiver.
    # Temporarily activates that window and positions/restores the cursor. Close the normal probe first.
    $smooveResult = Join-Path $smooveRoot ('out\' + $ReportName)
    if (Test-Path -LiteralPath $smooveResult) { Remove-Item -LiteralPath $smooveResult }
    $smooveDll = Join-Path $smooveRoot 'src\Smoove.Probe\bin\Release\net10.0-windows\Smoove.Probe.dll'
    if ($Published) {
        $smooveExecutable = Join-Path $smooveRoot 'out\prototype\Smoove.Probe.exe'
        $smooveProcess = Start-Process -FilePath $smooveExecutable -ArgumentList @($Mode, ('"' + $smooveResult + '"')) -PassThru -WindowStyle Hidden
    } else {
        $smooveProcess = Start-Process -FilePath $smooveDotnet -ArgumentList @(('"' + $smooveDll + '"'), $Mode, ('"' + $smooveResult + '"')) -PassThru -WindowStyle Hidden
    }
    if (-not $smooveProcess.WaitForExit(55000)) { $smooveProcess.Kill(); throw 'Native check timed out' }
    if (-not (Test-Path -LiteralPath $smooveResult)) { throw 'Native check produced no evidence' }
    $smooveEvidence = Get-Content -LiteralPath $smooveResult -Raw
    Write-Output $smooveEvidence
    if ($smooveProcess.ExitCode -ne 0 -or -not $smooveEvidence.StartsWith('PASS:')) { throw 'Native input check failed' }
}
if ($NativeInput) { Invoke-SmooveInputCheck '--integration-check' 'native-input-check.txt' }
if ($YandexBrowser) { Invoke-SmooveInputCheck '--integration-browser' 'yandex-input-check.txt' }
if ($TelegramChat) { Invoke-SmooveInputCheck '--integration-telegram' 'telegram-chat-check.txt' }
if ($ExplorerFiles) { Invoke-SmooveInputCheck '--integration-explorer' 'explorer-check.txt' }
