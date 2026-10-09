using Microsoft.UI.Xaml.Media;
using Smoove.Probe;
namespace Smoove.Settings;
public partial class App : Application
{
    private Window? _window;
    private SettingsHost? _host;
    private System.Threading.Mutex? _instance;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        _instance = new(true, "Local\\Smoove.Probe.Prototype", out bool first);
        if (!first) { Exit(); return; }
        _host = new SettingsHost();
        _window = new Window { Title = "Smoove — Настройки", SystemBackdrop = new MicaBackdrop() };
        var page = new MainPage(_host);
        page.SetOwner(_window);
        _window.Content = page;
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 900));
        _window.AppWindow.Closing += (_, args) => { args.Cancel = true; _window.AppWindow.Hide(); };
        _host.OpenRequested += () => page.DispatcherQueue.TryEnqueue(() => { _window.AppWindow.Show(); _window.Activate(); });
        _host.ExitRequested += () => page.DispatcherQueue.TryEnqueue(() => Exit());
        page.ExitRequested += () => { _host.Dispose(); _instance.Dispose(); Exit(); };
        _window.Activate();
    }
}
