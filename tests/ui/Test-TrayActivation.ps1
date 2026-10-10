param([string]$Executable='D:\Coding\Smoove 2.0\out\app\Smoove.Settings.exe',[switch]$TrayCallback,[switch]$ExitAfterTest)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms
Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;
public static class TrayActivationNative {
 [StructLayout(LayoutKind.Sequential)]public struct Point{public int X,Y;}
 [StructLayout(LayoutKind.Sequential)]public struct Rect{public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)]public struct Mouse{public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
 [StructLayout(LayoutKind.Explicit,Size=40)]public struct Input{[FieldOffset(0)]public uint Type;[FieldOffset(8)]public Mouse Mouse;}
 [DllImport("user32.dll")]public static extern int GetSystemMetrics(int index);
 [DllImport("user32.dll")]public static extern bool GetCursorPos(out Point p);
 [DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern int GetWindowText(IntPtr h,System.Text.StringBuilder text,int size);
 [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")]public static extern uint SendInput(uint n,Input[] data,int size);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
 [DllImport("user32.dll")]public static extern bool DestroyWindow(IntPtr h);
 public static void Click(int x,int y,bool right){SetCursorPos(x,y);var a=new Input[2];a[0].Mouse.Flags=right?8u:2u;a[1].Mouse.Flags=right?16u:4u;if(SendInput(2,a,40)!=2)throw new Exception("Click input failed");}
}
"@
$previousDpi=[TrayActivationNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
$oldCursor=New-Object TrayActivationNative+Point
[TrayActivationNative]::GetCursorPos([ref]$oldCursor)|Out-Null
$cover=[IntPtr]::Zero
$p=Get-Process Smoove.Settings -ErrorAction SilentlyContinue | Where-Object Path -eq $Executable
if(!$p){throw 'Run the target Smoove build before this check'}
$processId=$p.Id
Add-Type -Path (Join-Path $PSScriptRoot "TrayProbe.cs")
function Find-Element($predicate){
 foreach($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)){
  $current=$window.Current
  if($current.ProcessId -eq $processId -or $current.ClassName -match 'Shell_TrayWnd|Overflow'){
   foreach($element in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)){
    if(&$predicate $element){return $element}
   }
  }
 }
 return $null
}
function Click-Element($element,[bool]$right=$false){
 if(!$element){throw 'UI element missing'}
 $r=$element.Current.BoundingRectangle
 if($r.IsEmpty -or $r.Width -le 0){throw 'UI element has no clickable bounds'}
 [TrayActivationNative]::Click([int]($r.Left+$r.Width/2),[int]($r.Top+$r.Height/2),$right)
 Start-Sleep -Milliseconds 250
}
function Open-FromTray {
 if($TrayCallback){
 [SmooveTrayProbe]::EnumWindows({param($h,$param) $id=0;[SmooveTrayProbe]::GetWindowThreadProcessId($h,[ref]$id)|Out-Null;$s=New-Object Text.StringBuilder 256;[SmooveTrayProbe]::GetClassName($h,$s,256)|Out-Null;if($id -eq $processId -and $s.ToString() -like 'WindowsForms10.Window.0.*'){[SmooveTrayProbe]::SendMessage($h,0x800,[IntPtr]1,[IntPtr]0x205)|Out-Null};return $true},[IntPtr]::Zero)|Out-Null
 Start-Sleep -Milliseconds 400
 }else{
 [TrayActivationNative]::SetCursorPos([TrayActivationNative]::GetSystemMetrics(0)-200,[TrayActivationNative]::GetSystemMetrics(1)-1)|Out-Null
 Start-Sleep -Milliseconds 800
 $icon=Find-Element {param($e) $e.Current.ClassName -eq 'SystemTray.NormalButton' -and $e.Current.Name -eq 'Smoove'}
 if(!$icon){
  $overflow=Find-Element {param($e) $e.Current.ClassName -eq 'SystemTray.NormalButton' -and $e.Current.Name -match 'скрытые значки|hidden icons'}
  Click-Element $overflow
  $icon=Find-Element {param($e) $e.Current.Name -eq 'Smoove' -and $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button}
 }
 Click-Element $icon $true
 }
 $item=Find-Element {param($e) $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem -and $e.Current.Name -eq 'Настройки'}
 if($TrayCallback){([System.Windows.Automation.InvokePattern]$item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}else{Click-Element $item}
 Start-Sleep -Milliseconds 400
}
try {
 $script:handle=[IntPtr]::Zero
 [SmooveTrayProbe]::EnumWindows({param($h,$unused) $id=0;[SmooveTrayProbe]::GetWindowThreadProcessId($h,[ref]$id)|Out-Null;$title=New-Object Text.StringBuilder 256;[TrayActivationNative]::GetWindowText($h,$title,256)|Out-Null;if($id -eq $processId -and $title.ToString() -eq 'Smoove — Настройки'){$script:handle=$h};return $true},[IntPtr]::Zero)|Out-Null
 if($handle -eq [IntPtr]::Zero){
  foreach($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)){if($window.Current.ProcessId -eq $processId -and $window.Current.Name -eq 'Smoove — Настройки'){$handle=[IntPtr]$window.Current.NativeWindowHandle}}
 }
 if($cover -ne [IntPtr]::Zero){[TrayActivationNative]::DestroyWindow($cover)|Out-Null;$cover=[IntPtr]::Zero}
 $r=New-Object TrayActivationNative+Rect
 [TrayActivationNative]::GetWindowRect($handle,[ref]$r)|Out-Null
 $cover=[TrayActivationNative]::CreateWindowEx(0,'STATIC','Smoove activation check',0x10CF0000,$r.Left,$r.Top,$r.Right-$r.Left,$r.Bottom-$r.Top,[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero)
 [TrayActivationNative]::SetForegroundWindow($cover)|Out-Null
 [TrayActivationNative]::Click($r.Left+50,$r.Top+50,$false)
 [System.Windows.Forms.Application]::DoEvents()
 Start-Sleep -Milliseconds 100
 Start-Sleep -Milliseconds 300
 [TrayActivationNative]::ShowWindow($handle,6)|Out-Null
 if(![TrayActivationNative]::IsIconic($handle)){throw 'Preparation failed: window not minimized'}
 Open-FromTray
 if([TrayActivationNative]::IsIconic($handle) -or [TrayActivationNative]::GetForegroundWindow() -ne $handle){throw ('Minimized restore failed: iconic='+[TrayActivationNative]::IsIconic($handle)+' foreground='+[TrayActivationNative]::GetForegroundWindow()+' expected='+$handle)}
 'PASS: tray activation and accessible Settings action restored minimized window to foreground'
 if($cover -ne [IntPtr]::Zero){[TrayActivationNative]::DestroyWindow($cover)|Out-Null;$cover=[IntPtr]::Zero}
 $r=New-Object TrayActivationNative+Rect
 [TrayActivationNative]::GetWindowRect($handle,[ref]$r)|Out-Null
 $cover=[TrayActivationNative]::CreateWindowEx(0,'STATIC','Smoove activation check',0x10CF0000,$r.Left,$r.Top,$r.Right-$r.Left,$r.Bottom-$r.Top,[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero)
 if($cover -eq [IntPtr]::Zero){throw 'Cover window creation failed'}
 [TrayActivationNative]::SetForegroundWindow($cover)|Out-Null
 [TrayActivationNative]::Click($r.Left+50,$r.Top+50,$false)
 [System.Windows.Forms.Application]::DoEvents()
 Start-Sleep -Milliseconds 100
 if([TrayActivationNative]::GetForegroundWindow() -eq $handle){throw 'Preparation failed: settings still foreground'}
 Open-FromTray
 if([TrayActivationNative]::GetForegroundWindow() -ne $handle){throw ('Covered activation failed: foreground='+[TrayActivationNative]::GetForegroundWindow()+' expected='+$handle)}
 'PASS: tray activation and accessible Settings action brought covered settings window to foreground'
}finally{
 if($cover -ne [IntPtr]::Zero -and ![TrayActivationNative]::DestroyWindow($cover)){Write-Warning 'Cover window cleanup failed'}
 [TrayActivationNative]::SetCursorPos($oldCursor.X,$oldCursor.Y)|Out-Null
 [TrayActivationNative]::SetThreadDpiAwarenessContext($previousDpi)|Out-Null
}
