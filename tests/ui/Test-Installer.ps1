param([Parameter(Mandatory=$true)][string]$Installer,[switch]$SkipDesktopShortcut,[string]$PreviousStartupExe='')
$ErrorActionPreference='Stop'
$smooveRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$smooveArtifacts=Join-Path $smooveRoot 'out\installer-check'
$smooveTarget=Join-Path $smooveArtifacts 'custom-folder\Smoove'
$smooveDesktop=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Smoove.lnk'
$smooveGroup=Join-Path ([Environment]::GetFolderPath('Programs')) 'Smoove'
$smooveStartShortcut=Join-Path $smooveGroup 'Smoove.lnk'
$smooveShell=New-Object -ComObject WScript.Shell
$smooveOriginalStart=if(Test-Path $smooveStartShortcut){$shortcut=$smooveShell.CreateShortcut($smooveStartShortcut);[pscustomobject]@{TargetPath=$shortcut.TargetPath;Arguments=$shortcut.Arguments;WorkingDirectory=$shortcut.WorkingDirectory;IconLocation=$shortcut.IconLocation;WindowStyle=$shortcut.WindowStyle;Description=$shortcut.Description;Hotkey=$shortcut.Hotkey}}else{$null}
$smooveSettings=Join-Path $env:LOCALAPPDATA 'Smoove\settings.json'
$smooveRunKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$smooveUninstallKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{D73C1A2C-AB88-4A0F-9D83-88B7B7F62C0E}_is1'
if(((!$SkipDesktopShortcut) -and (Test-Path $smooveDesktop)) -or (Test-Path $smooveUninstallKey) -or (Test-Path $smooveTarget)) {
    throw 'Existing installation/shortcut/test directory found; refusing to overwrite user state.'
}
New-Item -ItemType Directory -Path $smooveArtifacts -Force | Out-Null
$smooveSavedRun=(Get-ItemProperty -LiteralPath $smooveRunKey -Name Smoove -ErrorAction SilentlyContinue).Smoove
$smooveOriginalHash=if(Test-Path $smooveSettings){(Get-FileHash -LiteralPath $smooveSettings).Hash}else{$null}
function Run-Setup([string]$File,[string[]]$Arguments) {
    $process=Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    if(!$process.WaitForExit(60000)){throw "Installer did not complete in 60s; PID=$($process.Id), logs=$smooveArtifacts"}
    if($process.ExitCode -ne 0){throw "Installer exit=$($process.ExitCode); logs=$smooveArtifacts"}
}
try {
    Get-Process Smoove.Settings -ErrorAction SilentlyContinue | Where-Object {$_.Path -like "$smooveRoot\out\*"} | Stop-Process
    if($PreviousStartupExe){
        if(!(Test-Path -LiteralPath $PreviousStartupExe)){throw "Previous startup executable missing: $PreviousStartupExe"}
        Set-ItemProperty -LiteralPath $smooveRunKey -Name Smoove -Value ('"'+(Resolve-Path -LiteralPath $PreviousStartupExe).Path+'" --startup')
    }
    $taskArgument=if($SkipDesktopShortcut){'/TASKS='}else{'/TASKS=desktopicon'}
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LANG=english',('/DIR="'+$smooveTarget+'"'),$taskArgument,('/LOG="'+(Join-Path $smooveArtifacts 'install.log')+'"'))
    Run-Setup $Installer $arguments
    $exe=Join-Path $smooveTarget 'Smoove.Settings.exe'
    if($PreviousStartupExe -and (Get-ItemProperty -LiteralPath $smooveRunKey -Name Smoove).Smoove -ne ('"'+$exe+'" --startup')){throw 'Portable startup entry was not migrated to the installed executable'}
    foreach($relative in @('Smoove.Settings.exe','Smoove.Settings.dll','Smoove.Probe.dll','Smoove.Core.dll','coreclr.dll','Microsoft.ui.xaml.dll','Assets\Smoove.ico','unins000.exe')) {
        if(!(Test-Path (Join-Path $smooveTarget $relative))){throw "Missing installed dependency: $relative; logs=$smooveArtifacts"}
    }
    $shell=$smooveShell
    $shortcutPaths=@((Join-Path $smooveGroup 'Smoove.lnk'))
    if(!$SkipDesktopShortcut){$shortcutPaths+=@($smooveDesktop)}
    foreach($shortcut in $shortcutPaths) {
        if(!(Test-Path $shortcut) -or $shell.CreateShortcut($shortcut).TargetPath -ne $exe){throw "Shortcut missing or wrong target: $shortcut"}
    }
    if((Get-ItemProperty $smooveUninstallKey).InstallLocation.TrimEnd('\') -ne $smooveTarget){throw 'Custom install folder not recorded'}
    $launchShortcut=if($SkipDesktopShortcut){Join-Path $smooveGroup 'Smoove.lnk'}else{$smooveDesktop}
    $application=Start-Process -FilePath $launchShortcut -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
    $installed=Get-Process Smoove.Settings | Where-Object Path -eq $exe
    if(!$installed -or !$installed.Responding){throw 'Desktop shortcut did not launch installed program'}
    Write-Output 'PASS: custom install directory, bundled runtime, Start/Desktop shortcuts, installed application startup'
    $smooveInstalledBytes=(Get-ChildItem -LiteralPath $smooveTarget -File -Recurse | Measure-Object Length -Sum).Sum
    Write-Output "Installed files: $smooveInstalledBytes bytes"
    $installed | Stop-Process
    Run-Setup $Installer $arguments
    if(!(Test-Path $exe)){throw 'Update lost program files'}
    if($smooveOriginalHash -and (Get-FileHash $smooveSettings).Hash -ne $smooveOriginalHash){throw 'User settings changed'}
    Write-Output 'PASS: repeat installation/update preserves settings and install folder'
    Set-ItemProperty -LiteralPath $smooveRunKey -Name Smoove -Value ('"'+$exe+'" --startup')
    Run-Setup (Join-Path $smooveTarget 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $smooveArtifacts 'uninstall.log')+'"'))
    if((Test-Path $exe) -or (!$SkipDesktopShortcut -and (Test-Path $smooveDesktop)) -or (Test-Path (Join-Path $smooveGroup 'Smoove.lnk')) -or (Test-Path $smooveUninstallKey)){throw 'Uninstall left executable/shortcut/registration'}
    if((Get-ItemProperty -LiteralPath $smooveRunKey -Name Smoove -ErrorAction SilentlyContinue).Smoove){throw 'Uninstall left owned startup entry'}
    if($smooveOriginalHash -and (Get-FileHash $smooveSettings).Hash -ne $smooveOriginalHash){throw 'Uninstall changed user settings'}
    Write-Output "PASS: uninstall removes files, shortcuts and owned startup; settings preserved; logs=$smooveArtifacts"
} finally {
    if($null -eq $smooveSavedRun){Remove-ItemProperty -LiteralPath $smooveRunKey -Name Smoove -ErrorAction SilentlyContinue}
    else {Set-ItemProperty -LiteralPath $smooveRunKey -Name Smoove -Value $smooveSavedRun}
    if($smooveOriginalStart){
        New-Item -ItemType Directory -Path $smooveGroup -Force | Out-Null
        $shortcut=$smooveShell.CreateShortcut($smooveStartShortcut)
        $shortcut.TargetPath=$smooveOriginalStart.TargetPath
        $shortcut.Arguments=$smooveOriginalStart.Arguments
        $shortcut.WorkingDirectory=$smooveOriginalStart.WorkingDirectory
        $shortcut.IconLocation=$smooveOriginalStart.IconLocation
        $shortcut.WindowStyle=$smooveOriginalStart.WindowStyle
        $shortcut.Description=$smooveOriginalStart.Description
        $shortcut.Hotkey=$smooveOriginalStart.Hotkey
        $shortcut.Save()
    }elseif(Test-Path $smooveStartShortcut){Remove-Item -LiteralPath $smooveStartShortcut}
    if(Test-Path $smooveUninstallKey){Write-Warning "Test installation remains at $smooveTarget; inspect logs before cleanup."}
}
