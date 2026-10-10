using Microsoft.Win32;

namespace Smoove.Settings;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Smoove";
    private static string Command => $"\"{Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь программы.")}\" --startup";

    internal static bool Enabled
    {
        get
        {
            using var key=Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(RunKey,true);
        if(enabled)
        {
            string command=Command;
            if(command.Length>260)throw new InvalidOperationException("Путь слишком длинный для автозапуска Windows. Переместите Smoove в папку с более коротким путём.");
            key.SetValue(ValueName,command,RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName,false);
    }
}
