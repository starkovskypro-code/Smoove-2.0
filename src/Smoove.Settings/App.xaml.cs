using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Smoove.Probe;
using System.Runtime.InteropServices;
namespace Smoove.Settings;
public partial class App : Application
{
    private Window? _window;
    private MainPage? _page;
    private SettingsHost? _host;
    private System.Threading.Mutex? _instance;
    private TrayMenuWindow? _trayMenu;
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        _instance = new(true, "Local\\Smoove.Probe.Prototype", out bool first);
        if (!first) { Exit(); return; }
        _host = new SettingsHost();
        _page = new MainPage(_host);
        _window = CreateSettingsWindow();
        _host.OpenRequested += () => _page.DispatcherQueue.TryEnqueue(ShowSettings);
        _host.ExitRequested += () => _page.DispatcherQueue.TryEnqueue(ExitApplication);
        _host.TrayMenuRequested += (x,y) => _page.DispatcherQueue.TryEnqueue(() =>
        {
            if(_trayMenu is not null && !WindowDesktop.IsCurrent(_trayMenu))
            {
                _trayMenu.Close();
                _trayMenu=null;
            }
            _trayMenu ??= new TrayMenuWindow(ShowSettings,_page.ToggleSmoothing,ExitApplication);
            _trayMenu.ShowAt(x,y,_host.Enabled,_page.ActualTheme);
        });
        _page.ExitRequested += ExitApplication;
        if(!Environment.GetCommandLineArgs().Contains("--startup",StringComparer.OrdinalIgnoreCase))ShowSettings();
    }

    private Window CreateSettingsWindow(Windows.Graphics.SizeInt32? size=null)
    {
        var window = new Window { Title = "Smoove — Настройки", SystemBackdrop = new MicaBackdrop() };
        window.AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","Smoove.ico"));
        _page!.SetOwner(window);
        window.Content=_page;
        var display=DisplayArea.GetFromWindowId(window.AppWindow.Id,DisplayAreaFallback.Primary);
        var work=display.WorkArea;
        var requested=size??new Windows.Graphics.SizeInt32(800,1120);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(requested.Width,work.Width),Math.Min(requested.Height,work.Height)));
        window.AppWindow.Move(new Windows.Graphics.PointInt32(work.X+(work.Width-window.AppWindow.Size.Width)/2,work.Y+(work.Height-window.AppWindow.Size.Height)/2));
        window.AppWindow.Closing += HideSettings;
        return window;
    }

    private void HideSettings(AppWindow sender,AppWindowClosingEventArgs args)
    {
        args.Cancel=true;
        sender.Hide();
    }

    private void ShowSettings()
    {
        if(_window is not null && !WindowDesktop.IsCurrent(_window))
        {
            var old=_window;
            var size=old.AppWindow.Size;
            old.Content=null;
            _window=CreateSettingsWindow(size);
            old.AppWindow.Closing-=HideSettings;
            old.Close();
        }
        _window!.AppWindow.Show();
        if(_window.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State==OverlappedPresenterState.Minimized)
            presenter.Restore();
        _window.Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window));
    }

    private void ExitApplication()
    {
        _host?.Dispose();
        _instance?.Dispose();
        Exit();
    }
}
