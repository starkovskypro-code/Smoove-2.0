using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Smoove.Core;

namespace Smoove.Settings;

internal sealed record ApplicationChoice(string Name, string Path);
internal static class ApplicationCatalog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo { public nint Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=80)] public string TypeName; }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)] private static extern nuint SHGetFileInfoW(string path,uint attributes,out FileInfo info,uint size,uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);

    public static ApplicationChoice[] Running()
    {
        var entries = new Dictionary<string,ApplicationChoice>(StringComparer.OrdinalIgnoreCase);
        int session = Process.GetCurrentProcess().SessionId;
        using var currentIdentity=System.Security.Principal.WindowsIdentity.GetCurrent();
        var currentUser=currentIdentity.User;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
        foreach(var process in Process.GetProcesses())
        {
            using(process)
            try
            {
                if(process.SessionId != session || process.Id == Environment.ProcessId) continue;
                if(!OpenProcessToken(process.Handle,8,out nint token))continue;
                try {using var identity=new System.Security.Principal.WindowsIdentity(token);if(identity.User!=currentUser)continue;}
                finally{CloseHandle(token);}
                string? path = process.MainModule?.FileName;
                if(path is null || !File.Exists(path) || path.StartsWith(windows,StringComparison.OrdinalIgnoreCase)) continue;
                path=ApplicationExclusion.Normalize(path);
                string? description=FileVersionInfo.GetVersionInfo(path).FileDescription;
                string name=string.IsNullOrWhiteSpace(description)?System.IO.Path.GetFileName(path):description;
                entries.TryAdd(path,new(name,path));
            }
            catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return entries.Values.OrderBy(e=>e.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static async Task<BitmapImage?> Icon(string path)
    {
        return await Task.Run(() =>
        {
            if(SHGetFileInfoW(path,0,out var info,(uint)Marshal.SizeOf<FileInfo>(),0x100 | 0x1)==0 || info.Icon==0) return null;
            try
            {
                using var icon=System.Drawing.Icon.FromHandle(info.Icon);
                using var bitmap=icon.ToBitmap();
                using var stream=new MemoryStream();
                bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);
                return stream.ToArray();
            }
            finally { DestroyIcon(info.Icon); }
        }) is { } bytes ? await FromBytes(bytes) : null;
    }
    private static async Task<BitmapImage> FromBytes(byte[] bytes)
    {
        using var stream=new InMemoryRandomAccessStream();
        using(var writer=new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
        stream.Seek(0);
        var image=new BitmapImage(); await image.SetSourceAsync(stream); return image;
    }
}
