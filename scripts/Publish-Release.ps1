param([string]$Version = '0.5.0-beta.2')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version' }
$smooveRoot = Split-Path -Parent $PSScriptRoot
$smooveDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $smooveDotnet)) { $smooveDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$smooveRelease = Join-Path $smooveRoot ('out\release\' + $Version)
if (Test-Path -LiteralPath $smooveRelease) { throw 'Release directory already exists; use a fresh version or preserve/remove it explicitly' }
$smooveName = 'Smoove-' + $Version + '-win-x64'
$smooveStage = Join-Path $smooveRelease $smooveName
New-Item -ItemType Directory -Path $smooveStage -Force | Out-Null
& $smooveDotnet publish (Join-Path $smooveRoot 'src\Smoove.Settings\Smoove.Settings.csproj') -c Release -p:Platform=x64 -p:PublishTrimmed=false "-p:Version=$Version" --self-contained true -o $smooveStage --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed' }
# Keep runtime dependencies, omit diagnostic entry point and developer symbols.
Get-ChildItem -LiteralPath $smooveStage -Recurse -File -Filter '*.pdb' | Remove-Item
$smooveProbeExe = Join-Path $smooveStage 'Smoove.Probe.exe'
if (Test-Path -LiteralPath $smooveProbeExe) { Remove-Item -LiteralPath $smooveProbeExe }
Copy-Item -LiteralPath (Join-Path $smooveRoot 'docs\USER-GUIDE.md') -Destination (Join-Path $smooveStage 'USER-GUIDE.md')
@"
Smoove $Version — Windows 11 x64

1. Распакуйте архив целиком.
2. Запустите Smoove.Settings.exe.
3. Закрытие настроек оставляет Smoove в трее. Для завершения выберите «Выход».

.NET и Windows App SDK входят в папку. Не перемещайте один EXE отдельно.
Рекомендуется: расстояние 1x, ускорение 60%, сглаживание 110%, частота 240 Гц.
Настройки: %LOCALAPPDATA%\Smoove\settings.json.
Это beta без установщика/автообновления. Программа пока не подписана сертификатом издателя.
Shift + обычное колесо — плавная горизонтальная прокрутка с общими настройками. Совместимость зависит от приложения; физическое горизонтальное колесо остаётся исходным.

Инструкция: USER-GUIDE.md
Проект и обновления: https://github.com/starkovskypro-code/Smoove-2.0
"@ | Set-Content -LiteralPath (Join-Path $smooveStage 'START-HERE.txt')
$smooveRequired = @('Smoove.Settings.exe','Smoove.Settings.dll','Smoove.Probe.dll','Smoove.Core.dll','Microsoft.UI.Xaml.dll','coreclr.dll','START-HERE.txt','USER-GUIDE.md','Assets/Smoove.ico')
foreach ($smooveFile in $smooveRequired) {
    if (!(Test-Path -LiteralPath (Join-Path $smooveStage $smooveFile))) { throw ('Missing release dependency: ' + $smooveFile) }
}
$smoovePrivate = Get-ChildItem -LiteralPath $smooveStage -Recurse -File | Where-Object { $_.Name -in @('settings.json','settings.json.bak','.env') -or $_.Extension -in @('.pdb','.log','.cs','.ps1') }
if ($smoovePrivate) { throw 'Release contains private data, logs, source files or debugging symbols' }
$smooveZip = Join-Path $smooveRelease ($smooveName + '.zip')
[System.IO.Compression.ZipFile]::CreateFromDirectory($smooveStage, $smooveZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$smooveArchive = [System.IO.Compression.ZipFile]::OpenRead($smooveZip)
try {
    foreach ($smooveFile in $smooveRequired) {
        if ($smooveArchive.Entries.FullName -notcontains ($smooveName + '/' + $smooveFile)) { throw ('Archive is incomplete: ' + $smooveFile) }
    }
} finally { $smooveArchive.Dispose() }
$smooveHash = (Get-FileHash -LiteralPath $smooveZip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $smooveRelease 'SHA256SUMS.txt'), $smooveHash + '  ' + $smooveName + ".zip`n", [Text.UTF8Encoding]::new($false))
Write-Output ('PASS: release archive dependencies, excluded private data and SHA256; ' + $smooveZip)
