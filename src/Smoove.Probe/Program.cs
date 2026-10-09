namespace Smoove.Probe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool browserCheck = args.Length == 2 && args[0] == "--integration-browser";
        bool telegramCheck = args.Length == 2 && args[0] == "--integration-telegram";
        bool explorerCheck=args.Length==2 && args[0]=="--integration-explorer";
        string? integrationPath = args.Length == 2 && (args[0] == "--integration-check" || browserCheck || telegramCheck || explorerCheck) ? Path.GetFullPath(args[1]) : null;
        if (integrationPath is not null) File.WriteAllText(integrationPath + ".progress.log", "Main entered\n");
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, "Local\\Smoove.Probe.Prototype", out bool first);
        if (!first)
        {
            if (integrationPath is not null) File.WriteAllText(integrationPath, "FAIL: another probe instance is running");
            else MessageBox.Show("Прототип Smoove уже запущен. Откройте его из трея.", "Smoove");
            Environment.ExitCode = 2;
            return;
        }
        try { Application.Run(new ProbeForm(integrationPath, browserCheck, telegramCheck,explorerCheck:explorerCheck)); }
        catch (Exception ex)
        {
            if (integrationPath is not null) File.WriteAllText(integrationPath, $"FAIL: {ex}");
            else MessageBox.Show(ex.Message, "Smoove — запуск не удался", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }
}
