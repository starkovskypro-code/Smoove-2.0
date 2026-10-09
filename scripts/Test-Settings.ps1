param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
$smooveRoot = Split-Path -Parent $PSScriptRoot
if (!$Executable) { $Executable = Join-Path $smooveRoot 'out\app\Smoove.Settings.exe' }
$smooveRuntime = Join-Path $env:USERPROFILE '.dotnet\shared\Microsoft.WindowsDesktop.App\10.0.12'
foreach ($smooveAssembly in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $smooveRuntime $smooveAssembly)) | Out-Null
}
$smooveProcess = Get-Process -Name Smoove.Settings -ErrorAction SilentlyContinue
if (!$smooveProcess) { $smooveProcess = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden }
for ($smooveAttempt=0; $smooveAttempt -lt 50; $smooveAttempt++) {
    $smooveProcess.Refresh()
    if ($smooveProcess.HasExited -or $smooveProcess.MainWindowHandle -ne 0) { break }
    Start-Sleep -Milliseconds 100
}
$smooveProcess.Refresh()
if ($smooveProcess.MainWindowHandle -eq 0) { throw 'No native settings window' }
$smooveUi = [System.Windows.Automation.AutomationElement]::FromHandle($smooveProcess.MainWindowHandle)
function Find-SmooveControl([string]$Name, $Type) {
    $smooveCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $Type))
    $smooveControl = $smooveUi.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $smooveCondition)
    if (!$smooveControl) { throw ('Missing native control: ' + $Name) }
    return $smooveControl
}
$smooveSliders = @('Расстояние прокрутки','Ускорение при быстром вращении','Сглаживание серии шагов','Разгон','Инерция и остановка')
foreach ($smooveName in $smooveSliders) {
    $smooveSlider = Find-SmooveControl $smooveName ([System.Windows.Automation.ControlType]::Slider)
    $null = $smooveSlider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
}
$smooveAccel = (Find-SmooveControl 'Ускорение при быстром вращении' ([System.Windows.Automation.ControlType]::Slider)).GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
$smoovePrevious = $smooveAccel.Current.Value
try {
    $smooveAccel.SetValue(75)
    Start-Sleep -Milliseconds 300
    $smoovePath = Join-Path $env:LOCALAPPDATA 'Smoove\settings.json'
    $smooveSaved = Get-Content -LiteralPath $smoovePath -Raw | ConvertFrom-Json
    if ($smooveSaved.Acceleration -ne 75) { throw 'Slider change not persisted' }
    foreach ($smooveSection in @('Исключения','Диагностика','Настройки')) {
        $smooveItem = Find-SmooveControl $smooveSection ([System.Windows.Automation.ControlType]::ListItem)
        $smooveSelection = $smooveItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $smooveSelection.Select()
        if (!$smooveSelection.Current.IsSelected) { throw ('Navigation failed: ' + $smooveSection) }
    }
    # Restart this app to verify readback, without changing the input source or other apps.
    $smooveProcess.Kill()
    $smooveProcess.WaitForExit()
    $smooveProcess = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 1500
    $smooveProcess.Refresh()
    $smooveUi = [System.Windows.Automation.AutomationElement]::FromHandle($smooveProcess.MainWindowHandle)
    $smooveAccel = (Find-SmooveControl 'Ускорение при быстром вращении' ([System.Windows.Automation.ControlType]::Slider)).GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    if ($smooveAccel.Current.Value -ne 75) { throw 'Saved slider value did not survive restart' }
    Write-Output 'PASS: five native sliders, section navigation, live persistence and restart readback'
} finally { if ($smooveAccel) { $smooveAccel.SetValue($smoovePrevious) } }
