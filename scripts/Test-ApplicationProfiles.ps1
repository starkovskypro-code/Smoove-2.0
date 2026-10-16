param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
$smooveRoot = Split-Path -Parent $PSScriptRoot
if (!$Executable) { $Executable = Join-Path $smooveRoot 'out/app/Smoove.Settings.exe' }
$Executable = [IO.Path]::GetFullPath($Executable)
$smooveArtifacts = Join-Path $smooveRoot 'test-artifacts\application-profiles'
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
function Select-Choice([string]$Control,[string]$Choice) {
    $combo=Find-SmooveControl $Control ([System.Windows.Automation.ControlType]::ComboBox)
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 100
    (Find-SmooveControl $Choice ([System.Windows.Automation.ControlType]::ListItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Start-Sleep -Milliseconds 150
}
function Navigate-Section([string]$Name) {
    (Find-SmooveControl $Name ([System.Windows.Automation.ControlType]::ListItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 150
}
function Click-Button([string]$Name) {
    (Find-SmooveControl $Name ([System.Windows.Automation.ControlType]::Button)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 150
}
function Require([bool]$Condition,[string]$Message) { if(!$Condition){throw $Message} }
Add-Type @'
public static class SettingsKeyboard {
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size=40)]
    public struct Input {
        [System.Runtime.InteropServices.FieldOffset(0)] public uint Type;
        [System.Runtime.InteropServices.FieldOffset(8)] public ushort Key;
        [System.Runtime.InteropServices.FieldOffset(12)] public uint Flags;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
    public static void HomeEnter() {
        var inputs=new[]{new Input{Type=1,Key=0x24},new Input{Type=1,Key=0x24,Flags=2},new Input{Type=1,Key=0x0D},new Input{Type=1,Key=0x0D,Flags=2}};
        if(SendInput(4,inputs,40)!=4)throw new System.InvalidOperationException("Keyboard input failed");
    }
}
'@
$smooveFixture=$null
try {
    if(Get-Process Smoove.Settings,Smoove.Probe -ErrorAction SilentlyContinue){throw 'Close Smoove before this isolated check.'}
    $fixturePath=Join-Path $smooveRoot 'tests\Smoove.Integration\bin\Release\net10.0-windows\Code.exe'
    $fixtureSource=Join-Path (Split-Path $fixturePath) 'Smoove.Integration.exe'
    if(!(Test-Path -LiteralPath $fixtureSource)){throw 'Build Smoove.slnx in Release before running this UI check.'}
    Copy-Item -LiteralPath $fixtureSource -Destination $fixturePath -Force
    $smooveFixture=Start-Process -FilePath $fixturePath -ArgumentList '--fixture-ui' -PassThru -WindowStyle Hidden
    $values=@{Enabled=$true;Distance=1;Acceleration=60;Smooth=110;Rise=70;Coast=160;Frequency=2;External=$true;Excluded='';Theme=0;Version=4;AnimationTempo=1.7;Engine=0;Exclusions=@(@{Path='C:\Apps\Legacy.exe';Enabled=$false;LegacyName=$false},@{Path='old-editor';Enabled=$true;LegacyName=$true})}
    [IO.File]::WriteAllText($smoovePath,($values|ConvertTo-Json -Depth 8))
    Start-SmooveCheck
    $saved=Get-Content $smoovePath -Raw|ConvertFrom-Json
    Require ($saved.Version -eq 5 -and $saved.Applications.Count -eq 2 -and !$saved.Applications[0].Enabled -and $saved.Applications[1].LegacyName -and $saved.Applications[1].Mode -eq 2) 'Migration lost legacy exclusions'
    Navigate-Section 'Программы'
    Click-Button 'Выбрать из запущенных'
    Start-Sleep -Milliseconds 500
    $picker=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Запущенные программы — Smoove'))
    Require ($null -ne $picker) 'Picker missing'
    $main=$script:smooveUi;$script:smooveUi=$picker
    (Find-SmooveControl 'Поиск программы' ([System.Windows.Automation.ControlType]::Edit)).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($fixturePath)
    Start-Sleep -Milliseconds 150
    $list=$picker.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::List))
    $items=$list.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
    Require ($items.Count -eq 1) "Picker expected one fixture, actual=$($items.Count)"
    $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Click-Button 'Добавить выбранные'
    $script:smooveUi=$main
    Select-Choice 'Режим Code.exe' 'Свой профиль'
    Select-Choice 'Где сглаживать прокрутку' 'Только в выбранных программах'
    Capture-Smoove 'applications.png'
    Click-Button 'Настроить Code.exe'
    (Smoove-Slider 'Расстояние прокрутки').SetValue(2.5)
    (Smoove-Slider 'Темп анимации').SetValue(3.2)
    Select-SmooveEngine 'Smoothie — экспериментальная модель'
    (Smoove-Slider 'Эксперимент: расстояние').SetValue(3.1)
    Capture-Smoove 'custom-profile.png'
    Select-Choice 'Настройки для' 'Общие настройки'
    Require ([Math]::Abs((Smoove-Slider 'Расстояние прокрутки').Current.Value-1) -lt 1e-6) 'Custom overwrote global distance'
    Require ([Math]::Abs((Smoove-Slider 'Темп анимации').Current.Value-1.7) -lt 1e-6) 'Custom overwrote global tempo'
    Select-Choice 'Настройки для' 'Code.exe'
    Require ([Math]::Abs((Smoove-Slider 'Эксперимент: расстояние').Current.Value-3.1) -lt 1e-6) 'Scope switch loaded stale values'
    Select-Choice 'Частота обновления прокрутки' 'Авто — по монитору под курсором'
    $saved=Get-Content $smoovePath -Raw|ConvertFrom-Json;$app=$saved.Applications|Where-Object Path -eq $fixturePath
    Require ($saved.OnlySelected -and $saved.Frequency -eq 4 -and $saved.Engine -eq 0 -and $app.Settings.Engine -eq 1 -and [Math]::Abs($app.Settings.AnimationTempo-3.2) -lt 1e-6) 'Profile/scope/frequency did not persist'
    Stop-SmooveCheck;Start-SmooveCheck
    Navigate-Section 'Программы';Click-Button 'Настроить Code.exe'
    Require ([Math]::Abs((Smoove-Slider 'Эксперимент: расстояние').Current.Value-3.1) -lt 1e-6) 'Restart lost profile'
    Navigate-Section 'Программы'
    Select-Choice 'Режим Code.exe' 'Общие настройки'
    Select-Choice 'Режим Code.exe' 'Свой профиль'
    Click-Button 'Настроить Code.exe'
    Require ([Math]::Abs((Smoove-Slider 'Эксперимент: расстояние').Current.Value-3.1) -lt 1e-6) 'Mode toggle lost retained values'
    Navigate-Section 'Программы';Click-Button 'Удалить профиль Code.exe'
    $saved=Get-Content $smoovePath -Raw|ConvertFrom-Json
    Require ($saved.Applications.Count -eq 2 -and $saved.Engine -eq 0) 'Deleting current profile damaged global settings'
    Navigate-Section 'Настройки'
    Select-Choice 'Тема оформления' 'Тёмная'
    Navigate-Section 'Программы';Capture-Smoove 'applications-dark.png'
    Navigate-Section 'Настройки'
    $frequency=Find-SmooveControl 'Частота обновления прокрутки' ([System.Windows.Automation.ControlType]::ComboBox)
    $frequency.SetFocus();$frequency.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Require ([SettingsKeyboard]::GetForegroundWindow() -eq $script:smooveProcess.MainWindowHandle) 'Keyboard target is not the settings window'
    [SettingsKeyboard]::HomeEnter();Start-Sleep -Milliseconds 250
    Require ((Get-Content $smoovePath -Raw|ConvertFrom-Json).Frequency -eq 0) 'Frequency selection did not respond to keyboard'
    Stop-SmooveCheck
    foreach($invalid in @('{"Version":5,"Enabled":true,"Distance":1,"Acceleration":60,"Smooth":110,"Rise":70,"Coast":160,"Frequency":2,"External":true,"Excluded":"","Theme":0,"Applications":[null]}','{"Version":6,"Enabled":true,"Distance":1,"Acceleration":60,"Smooth":110,"Rise":70,"Coast":160,"Frequency":2,"External":true,"Excluded":"","Theme":0}')) {
        [IO.File]::WriteAllText($smoovePath,$invalid);Start-SmooveCheck;Start-Sleep -Milliseconds 200
        Require ([IO.File]::ReadAllText($smoovePath) -eq $invalid) 'Invalid/future settings file was overwritten'
        Stop-SmooveCheck
    }
    $result='PASS: v4 migration, picker/app modes, independent settings, switching/restart persistence, deletion, keyboard selection, light/dark UI and preservation of invalid/future settings'
    [IO.File]::WriteAllText((Join-Path $smooveArtifacts 'profiles-ui.txt'),$result);Write-Output $result
} catch {
    [IO.File]::WriteAllText((Join-Path $smooveArtifacts 'profiles-ui.txt'),"FAIL: $_");throw
} finally {
    Stop-SmooveCheck
    if($smooveFixture -and !$smooveFixture.HasExited){$smooveFixture.Kill();$smooveFixture.WaitForExit()}
    foreach($file in $smooveSnapshots.Keys){if($null -eq $smooveSnapshots[$file]){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file}}else{[IO.File]::WriteAllBytes($file,$smooveSnapshots[$file])}}
}
