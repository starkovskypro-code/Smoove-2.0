param(
    [string]$Version = '1.0.0',
    [string]$Compiler = ''
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid version' }
$smooveRoot = Split-Path -Parent $PSScriptRoot
if (!$Compiler) {
    $smooveCandidates = @(
        (Join-Path $smooveRoot 'out\tools\inno\compiler\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    )
    $Compiler = $smooveCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$Compiler) { throw 'Inno Setup compiler not found. Pass -Compiler with the path to ISCC.exe.' }
$smooveOutput = Join-Path $smooveRoot ('out\installer\' + $Version)
if (Test-Path -LiteralPath $smooveOutput) { throw 'Output directory already exists; use a fresh version.' }
$smooveStage = Join-Path $smooveOutput 'payload'
New-Item -ItemType Directory -Path $smooveStage -Force | Out-Null
$smooveDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $smooveDotnet)) { $smooveDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $smooveDotnet publish (Join-Path $smooveRoot 'src\Smoove.Settings\Smoove.Settings.csproj') -c Release -p:Platform=x64 -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" --self-contained true -o $smooveStage --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
# Remove only generated diagnostic artifacts, never user settings.
Get-ChildItem -LiteralPath $smooveStage -File -Recurse -Filter '*.pdb' | Remove-Item
$smooveProbe = Join-Path $smooveStage 'Smoove.Probe.exe'
if (Test-Path -LiteralPath $smooveProbe) { Remove-Item -LiteralPath $smooveProbe }
Copy-Item -LiteralPath (Join-Path $smooveRoot 'docs\USER-GUIDE.md') -Destination $smooveStage
$smooveRequired = @('Smoove.Settings.exe','Smoove.Settings.dll','Smoove.Probe.dll','Smoove.Core.dll','Microsoft.ui.xaml.dll','coreclr.dll','Assets\Smoove.ico')
foreach ($smooveFile in $smooveRequired) {
    if (!(Test-Path -LiteralPath (Join-Path $smooveStage $smooveFile))) { throw "Missing dependency: $smooveFile" }
}
$smoovePrivate = Get-ChildItem -LiteralPath $smooveStage -File -Recurse | Where-Object { $_.Name -in @('settings.json','settings.json.bak','.env') -or $_.Extension -in @('.pdb','.log','.cs','.ps1') }
if ($smoovePrivate) { throw 'Payload contains private data or developer files' }
& $Compiler "/DAppVersion=$Version" "/DSourceDir=$smooveStage" "/DInstallerOutput=$smooveOutput" (Join-Path $smooveRoot 'installer\Smoove.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$smooveExe = Join-Path $smooveOutput "Smoove-$Version-Setup-win-x64.exe"
$smooveHash = (Get-FileHash -LiteralPath $smooveExe -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $smooveOutput 'SHA256SUMS.txt'), "$smooveHash  $([IO.Path]::GetFileName($smooveExe))`n")
Write-Output "PASS: installer built: $smooveExe; bytes=$((Get-Item -LiteralPath $smooveExe).Length)"
