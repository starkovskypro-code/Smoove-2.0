param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -Path (Join-Path $PSScriptRoot 'TrayProbe.cs')
$process=Get-Process Smoove.Settings -ErrorAction Stop | Where-Object Path -eq (Resolve-Path -LiteralPath $Executable).Path | Select-Object -First 1
if(!$process){throw 'Target process was not found.'}
$processId=$process.Id
[SmooveTrayProbe]::EnumWindows({param($window,$unused) $id=0;[SmooveTrayProbe]::GetWindowThreadProcessId($window,[ref]$id)|Out-Null;$class=New-Object Text.StringBuilder 256;[SmooveTrayProbe]::GetClassName($window,$class,256)|Out-Null;if($id -eq $processId -and $class.ToString() -like 'WindowsForms10.Window.0.*'){[SmooveTrayProbe]::SendMessage($window,0x800,[IntPtr]1,[IntPtr]0x205)|Out-Null};return $true},[IntPtr]::Zero)|Out-Null
Start-Sleep -Milliseconds 300
$items=@()
foreach($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)){
    if($window.Current.ProcessId -eq $processId){$items+=@($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::MenuItem)))}
}
if($items.Count -ne 3){throw "Expected three Smoove tray commands; found $($items.Count)."}
([System.Windows.Automation.InvokePattern]$items[-1].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
if(!$process.WaitForExit(5000)){throw 'Smoove did not exit from tray.'}
Write-Output 'PASS: tray Exit command closed the target process.'
