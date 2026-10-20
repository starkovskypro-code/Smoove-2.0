using System.Reflection;
using Smoove.Probe;

namespace Smoove.Integration;

internal static class TrayMenuChecks
{
    internal static void Run(string report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        // Integration mode restricts hooks to this fixture; no wheel input or user settings are changed.
        foreach(bool host in new[]{false,true})
        {
            using var form=new ProbeForm(report+".fixture",settingsHost:host);
            form.ConfigureFromSettings(false,0.07,0.16,1,0,240,"");
            var tray=(NotifyIcon)typeof(ProbeForm).GetField("_tray",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
            ContextMenuStrip? menu=tray.ContextMenuStrip;
            if(host)
            {
                Require(menu is null,"WinUI host must not expose a WinForms menu");
                int requests=0,opened=0;
                Native.GetCursorPos(out var cursor);
                form.TrayMenuRequested+=(x,y)=>{Require(x==cursor.X && y==cursor.Y,"Tray menu coordinates changed");requests++;};
                form.SettingsRequested+=()=>opened++;
                typeof(NotifyIcon).GetMethod("OnMouseUp",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(tray,[new MouseEventArgs(MouseButtons.Left,1,0,0,0)]);
                Require(requests==0,"Left click opened the context menu");
                typeof(NotifyIcon).GetMethod("OnMouseUp",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(tray,[new MouseEventArgs(MouseButtons.Right,1,0,0,0)]);
                typeof(NotifyIcon).GetMethod("OnDoubleClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(tray,[EventArgs.Empty]);
                Require(requests==1 && opened==1,"WinUI menu or settings callback was lost");
                int exits=0;
                form.HostExitRequested+=()=>exits++;
                form.ExitFromSettings();
                Require(exits==1,"WinUI host exit callback was lost");
            }
            else
            {
                Require(menu is not null && menu.Items.Count==3,"Diagnostic menu must retain three commands");
                Require(menu!.Items[0].Text=="Настройки" && menu.Items[1].Text=="Сглаживать" && menu.Items[2].Text=="Выход","Diagnostic commands changed");
                var pause=(ToolStripMenuItem)menu.Items[1];
                Require(!pause.Checked && !form.HostEnabled,"Pause state did not reach diagnostic menu");
                pause.PerformClick();
                Require(pause.Checked && form.HostEnabled,"Diagnostic menu cannot enable smoothing");
                pause.PerformClick();
                Require(!pause.Checked && !form.HostEnabled,"Diagnostic menu cannot pause smoothing");
                form.ConfigureFromSettings(true,0.07,0.16,1,0,240,"");
                Require(pause.Checked && form.HostEnabled,"Diagnostic menu did not follow enabled state");
                menu.Items[2].PerformClick();
            }
            form.Dispose();
            Require(menu is null || menu.IsDisposed,"Diagnostic menu was not disposed");
        }
        File.WriteAllText(report,"PASS: WinUI host menu/settings/exit callbacks; diagnostic commands, bidirectional pause state and menu disposal");
    }

    private static void Require(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
    }
}
