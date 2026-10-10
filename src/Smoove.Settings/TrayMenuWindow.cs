using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Windowing;
using Windows.Foundation;

namespace Smoove.Settings;

// A tiny native owner lets the standard XAML flyout open while settings are hidden.
internal sealed class TrayMenuWindow : Window
{
    private readonly Grid _anchor = new();
    private readonly MenuFlyout _menu = new();
    private readonly ToggleMenuFlyoutItem _smoothing = new() { Text = "Плавная прокрутка", Icon=new SymbolIcon(Symbol.TouchPointer) };
    private bool _openSettings;

    internal TrayMenuWindow(Action settings, Action toggle, Action exit)
    {
        Title = "Smoove";
        Content = _anchor;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false,false);
        presenter.IsResizable=false;
        presenter.IsAlwaysOnTop=true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers=false;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1,1));
        _menu.ShouldConstrainToRootBounds=false;
        var open = new MenuFlyoutItem { Text="Настройки", Icon=new SymbolIcon(Symbol.Setting) };
        open.Click += (_,_)=>
        {
            _openSettings=true;
            _menu.Hide();
        };
        _smoothing.Click += (_,_)=>toggle();
        var close = new MenuFlyoutItem { Text="Выход", Icon=new SymbolIcon(Symbol.Cancel) };
        close.Click += (_,_)=>exit();
        _menu.Items.Add(open);
        _menu.Items.Add(_smoothing);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(close);
        _menu.Closed += (_,_)=>
        {
            AppWindow.Hide();
            if(!_openSettings)return;
            _openSettings=false;
            // Closed completes the popup's focus handoff before settings activate.
            DispatcherQueue.TryEnqueue(()=>settings());
        };
    }


    internal void ShowAt(int x,int y,bool enabled,ElementTheme theme)
    {
        _menu.Hide();
        _anchor.RequestedTheme=theme;
        _smoothing.IsChecked=enabled;
        AppWindow.Move(new Windows.Graphics.PointInt32(x,y));
        _anchor.Loaded -= OpenWhenLoaded;
        AppWindow.Show();
        Activate();
        if(_anchor.IsLoaded) OpenMenu();
        else _anchor.Loaded += OpenWhenLoaded;
    }

    private void OpenWhenLoaded(object sender,RoutedEventArgs args)
    {
        _anchor.Loaded -= OpenWhenLoaded;
        OpenMenu();
    }

    private void OpenMenu() => _menu.ShowAt(_anchor,new FlyoutShowOptions
    {
        Position=new Point(0,0),
        Placement=FlyoutPlacementMode.TopEdgeAlignedRight
    });
}
