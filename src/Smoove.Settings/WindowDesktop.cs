using System.Runtime.InteropServices;

namespace Smoove.Settings;

internal static class WindowDesktop
{
    [ComImport,Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint window,[MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig] int GetWindowDesktopId(nint window,out Guid desktop);
        [PreserveSig] int MoveWindowToDesktop(nint window,in Guid desktop);
    }

    internal static bool IsCurrent(Window window)
    {
        object? manager=null;
        try
        {
            manager=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"),true)!);
            int result=((IVirtualDesktopManager)manager!).IsWindowOnCurrentVirtualDesktop(WinRT.Interop.WindowNative.GetWindowHandle(window),out bool current);
            Marshal.ThrowExceptionForHR(result);
            return current;
        }
        catch(COMException ex)
        {
            System.Diagnostics.Debug.WriteLine("Virtual desktop query failed: "+ex.Message);
            return false; // Recreate our window rather than activate one on an unknown desktop.
        }
        finally { if(manager is not null)Marshal.ReleaseComObject(manager); }
    }
}
