param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
$smooveRoot = Split-Path -Parent $PSScriptRoot
if (!$Executable) { $Executable = Join-Path $smooveRoot 'out/app/Smoove.Settings.exe' }
$Executable = [IO.Path]::GetFullPath($Executable)
$smooveArtifacts = Join-Path $smooveRoot 'test-artifacts\engine-comparison'
New-Item -ItemType Directory -Path $smooveArtifacts -Force | Out-Null
$smooveRuntime = Join-Path $env:USERPROFILE '.dotnet\shared\Microsoft.WindowsDesktop.App\10.0.12'
foreach ($smooveAssembly in @('WindowsBase.dll', 'UIAutomationTypes.dll', 'UIAutomationClient.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $smooveRuntime $smooveAssembly)) | Out-Null
}
Add-Type -AssemblyName System.Drawing
Add-Type 'public static class EngineUiWindow { [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr window); }'
$smoovePath = Join-Path $env:LOCALAPPDATA 'Smoove\settings.json'
$smooveSnapshots = @{}
foreach ($smooveFile in @($smoovePath, ($smoovePath + '.bak'))) {
    $smooveSnapshots[$smooveFile] = if (Test-Path -LiteralPath $smooveFile) { [IO.File]::ReadAllBytes($smooveFile) } else { $null }
}
$smooveProcess = $null
function Start-SmooveCheck {
    if (Get-Process -Name Smoove.Settings,Smoove.Probe -ErrorAction SilentlyContinue) { throw 'Close Smoove before this UI check; no existing session will be stopped.' }
    $script:smooveProcess = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
    for ($smooveAttempt=0; $smooveAttempt -lt 60; $smooveAttempt++) {
        $script:smooveProcess.Refresh()
        if ($script:smooveProcess.HasExited) { throw 'Settings app exited during startup' }
        if ($script:smooveProcess.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($script:smooveProcess.MainWindowHandle -eq 0) { throw 'No settings window' }
    $script:smooveUi = [System.Windows.Automation.AutomationElement]::FromHandle($script:smooveProcess.MainWindowHandle)
    [EngineUiWindow]::SetForegroundWindow($script:smooveProcess.MainWindowHandle) | Out-Null
}
function Stop-SmooveCheck {
    if ($script:smooveProcess -and !$script:smooveProcess.HasExited) { $script:smooveProcess.Kill(); $script:smooveProcess.WaitForExit() }
}
function Find-SmooveControl([string]$Name, $Type) {
    $smooveCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $Type))
    $smooveControl = $script:smooveUi.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $smooveCondition)
    if (!$smooveControl) { throw "Missing native control: $Name" }
    return $smooveControl
}
function Select-SmooveEngine([string]$Name) {
    $smooveCombo = Find-SmooveControl 'Движок прокрутки' ([System.Windows.Automation.ControlType]::ComboBox)
    $smooveCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 100
    $smooveItem = Find-SmooveControl $Name ([System.Windows.Automation.ControlType]::ListItem)
    $smooveItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $smooveCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Start-Sleep -Milliseconds 150
}
function Smoove-Slider([string]$Name) {
    (Find-SmooveControl $Name ([System.Windows.Automation.ControlType]::Slider)).GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
}
function Capture-Smoove([string]$Name) {
    [EngineUiWindow]::SetForegroundWindow($script:smooveProcess.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 200
    $smooveRect = $script:smooveUi.Current.BoundingRectangle
    $smooveImage = [Drawing.Bitmap]::new([int]$smooveRect.Width, [int]$smooveRect.Height)
    $smooveGraphics = [Drawing.Graphics]::FromImage($smooveImage)
    try {
        $smooveGraphics.CopyFromScreen([int]$smooveRect.X,[int]$smooveRect.Y,0,0,$smooveImage.Size)
        $smooveImage.Save((Join-Path $smooveArtifacts $Name),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $smooveGraphics.Dispose(); $smooveImage.Dispose() }
}
try {
    Start-SmooveCheck
    Select-SmooveEngine 'Smoove — основной'
    (Smoove-Slider 'Темп анимации').SetValue(1.7)
    Capture-Smoove 'smoove.png'
    Select-SmooveEngine 'Smoothie — экспериментальная модель'
    $smooveControls = @('Эксперимент: расстояние','Эксперимент: сглаживание','Эксперимент: ускорение','Эксперимент: инерция')
    $smooveValues = @(2.5,135,1.3,1.7)
    for ($i=0; $i -lt 4; $i++) { (Smoove-Slider $smooveControls[$i]).SetValue($smooveValues[$i]) }
    Start-Sleep -Milliseconds 250
    $smooveSaved = Get-Content -LiteralPath $smoovePath -Raw | ConvertFrom-Json
    if ($smooveSaved.Engine -ne 1 -or [Math]::Abs($smooveSaved.AnimationTempo-1.7) -gt 1e-6 -or [Math]::Abs($smooveSaved.Experimental.DistancePerNotch-2.5) -gt 1e-6) {
        throw "Engine/profile storage: expected engine=1, tempo=1.7, distance=2.5; actual engine=$($smooveSaved.Engine), tempo=$($smooveSaved.AnimationTempo), distance=$($smooveSaved.Experimental.DistancePerNotch)"
    }
    Capture-Smoove 'experimental.png'
    Select-SmooveEngine 'Smoove — основной'
    if ([Math]::Abs((Smoove-Slider 'Темп анимации').Current.Value - 1.7) -gt 1e-6) { throw 'Switching engines lost Smoove settings' }
    Select-SmooveEngine 'Smoothie — экспериментальная модель'
    Stop-SmooveCheck
    Start-SmooveCheck
    if ((Get-Content -LiteralPath $smoovePath -Raw | ConvertFrom-Json).Engine -ne 1) { throw 'Selected engine lost after restart' }
    for ($i=0; $i -lt 4; $i++) {
        $smooveActual = (Smoove-Slider $smooveControls[$i]).Current.Value
        if ([Math]::Abs($smooveActual-$smooveValues[$i]) -gt 1e-6) { throw "Restart: $($smooveControls[$i]); expected=$($smooveValues[$i]), actual=$smooveActual" }
    }
    $smooveResult = 'PASS: dropdown selects both engines, four experimental sliders, independent profiles, persisted engine/profile after restart'
    [IO.File]::WriteAllText((Join-Path $smooveArtifacts 'ui-check.txt'),$smooveResult)
    Write-Output $smooveResult
} catch {
    [IO.File]::WriteAllText((Join-Path $smooveArtifacts 'ui-check.txt'),"FAIL: $_")
    throw
} finally {
    Stop-SmooveCheck
    foreach ($smooveFile in $smooveSnapshots.Keys) {
        if ($null -ne $smooveSnapshots[$smooveFile]) { [IO.File]::WriteAllBytes($smooveFile,$smooveSnapshots[$smooveFile]) }
        elseif (Test-Path -LiteralPath $smooveFile) { Remove-Item -LiteralPath $smooveFile }
    }
}
