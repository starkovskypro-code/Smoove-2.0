using System.Text.Json;
using Smoove.Probe;
using Smoove.Core;
namespace Smoove.Settings.Views;
public partial class MainPage : Page
{
    private readonly SettingsHost _host;
    private bool _ready;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Smoove", "settings.json");
    private sealed record Values(bool Enabled, double Distance, double Acceleration, double Smooth, double Rise, double Coast, int Frequency, bool External, string Excluded, int Theme, int Version = 2, ApplicationExclusion[]? Exclusions = null);
    private readonly List<ApplicationExclusion> _exclusions=[];
    private bool _saveAllowed=true;
    private Window? _owner;
    private RunningApplicationsWindow? _picker;
    internal void SetOwner(Window owner)=>_owner=owner;
    public event Action? ExitRequested;
    public MainPage(SettingsHost host)
    {
        _host = host;
        InitializeComponent();
        Defaults();
        Load();
        RenderExclusions();
        _ready = true;
        Apply();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) =>
        {
            StatusText.Text = _host.Status; StatisticsText.Text = _host.Statistics;
            if (Enabled.IsOn != _host.Enabled) { _ready=false; Enabled.IsOn=_host.Enabled; _ready=true; Apply(); }
        };
        _timer.Start();
    }
    private void Changed(object sender, object args) { if (_ready) Apply(); }
    private void Apply()
    {
        DistanceValue.Text = $"{Distance.Value:F1}×";
        AccelerationValue.Text = $"{Acceleration.Value:F0}%";
        SmoothValue.Text = $"{Smooth.Value:F0}%";
        RiseValue.Text = $"{Rise.Value:F0} мс";
        CoastValue.Text = $"{Coast.Value:F0} мс";
        int hz = int.Parse((string)((ComboBoxItem)Frequency.SelectedItem).Tag);
        _host.Configure(Enabled.IsOn, Rise.Value / 1000 * Smooth.Value / 100, Coast.Value / 1000 * Smooth.Value / 100,
            Distance.Value, Acceleration.Value / 100, hz, External.IsOn ? "" : "*");
        _host.SetExclusions(_exclusions.ToArray());
        Connection.Title = Enabled.IsOn ? "Smoove работает" : "Прокрутка на паузе";
        Connection.Severity = Enabled.IsOn ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        try
        {
            if(!_saveAllowed)return;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Values(Enabled.IsOn, Distance.Value, Acceleration.Value, Smooth.Value, Rise.Value, Coast.Value, Frequency.SelectedIndex, External.IsOn, "", ThemeChoice.SelectedIndex,2,_exclusions.ToArray())));
            if(File.Exists(SettingsPath))File.Copy(SettingsPath,SettingsPath+".bak",true);
            File.Move(temp, SettingsPath, true);
        }
        catch (IOException) { Connection.Message = "Не удалось сохранить настройки. Проверьте доступ к папке."; }
        catch (UnauthorizedAccessException) { Connection.Message = "Нет доступа к сохранению настроек."; }
    }
    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var v = JsonSerializer.Deserialize<Values>(File.ReadAllText(SettingsPath));
            if (v is null || v.Distance < 0.5 || v.Smooth < 50 || v.Rise < 5 || v.Coast < 5 ||
                !double.IsFinite(v.Distance+v.Acceleration+v.Smooth+v.Rise+v.Coast)) { _saveAllowed=false; return; }
            if(v.Version>2){_saveAllowed=false;Connection.Message="Настройки созданы более новой версией. Файл не будет перезаписан.";}
            foreach(var entry in v.Exclusions ?? ApplicationExclusion.Migrate(v.Excluded))
            {
                var validated=entry.LegacyName?entry:entry with {Path=ApplicationExclusion.Normalize(entry.Path)};
                if(!_exclusions.Any(e=>e.LegacyName==validated.LegacyName&&string.Equals(e.Path,validated.Path,StringComparison.OrdinalIgnoreCase)))_exclusions.Add(validated);
            }
            Enabled.IsOn=v.Enabled; Distance.Value=Math.Clamp(v.Distance,0.5,4); Acceleration.Value=Math.Clamp(v.Acceleration,0,100);
            Smooth.Value=Math.Clamp(v.Smooth,50,200); Rise.Value=Math.Clamp(v.Rise,5,300); Coast.Value=Math.Clamp(v.Coast,5,500);
            Frequency.SelectedIndex=Math.Clamp(v.Frequency,0,3); External.IsOn=v.External; ThemeChoice.SelectedIndex=Math.Clamp(v.Theme,0,2);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { _saveAllowed=false; Connection.Message="Не удалось прочитать настройки. Файл сохранён без изменений."; }
    }
    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _ready=false; Rise.Value=Preset.SelectedIndex==0?70:110; Coast.Value=Preset.SelectedIndex==0?160:280; _ready=true; Apply();
    }
    private void ResetClick(object sender,RoutedEventArgs e)
    {
        _ready=false; Defaults(); _ready=true; Apply();
    }
    private void Defaults()
    {
        Distance.Value=1; Acceleration.Value=60; Smooth.Value=110; Rise.Value=70; Coast.Value=160;
        Frequency.SelectedIndex=2; Enabled.IsOn=true; External.IsOn=true; Preset.SelectedIndex=0;
    }
    private void ThemeChanged(object sender,SelectionChangedEventArgs e)
    {
        RequestedTheme=ThemeChoice.SelectedIndex switch {1=>ElementTheme.Light,2=>ElementTheme.Dark,_=>ElementTheme.Default};
        if (_ready) Apply();
    }
    private void Navigate(NavigationView sender,NavigationViewSelectionChangedEventArgs e)
    {
        if (ScrollSection is null) return;
        string tag=(string)((NavigationViewItem)e.SelectedItem).Tag;
        ScrollSection.Visibility=tag=="scroll"?Visibility.Visible:Visibility.Collapsed;
        AppsSection.Visibility=tag=="apps"?Visibility.Visible:Visibility.Collapsed;
        DiagnosticsSection.Visibility=tag=="diagnostics"?Visibility.Visible:Visibility.Collapsed;
        TitleText.Text=tag switch {"apps"=>"Исключения","diagnostics"=>"Диагностика",_=>"Прокрутка"};
        SubtitleText.Text=tag switch {"apps"=>"Где применять сглаживание и как выглядит окно.","diagnostics"=>"Состояние обработки и технические счётчики.",_=>"Настройте движение под свой ритм. Изменения применяются сразу."};
    }
    private void ExitClick(object sender,RoutedEventArgs e) { _timer.Stop(); ExitRequested?.Invoke(); }
    private void ChooseRunning(object sender,RoutedEventArgs e)
    {
        if(_owner is null)return;
        if(_picker is not null){_picker.Activate();return;}
        _picker=new(_owner,AddExclusions);_picker.Closed+=(_,_)=>_picker=null;_picker.Activate();
    }
    private async void ChooseInstalled(object sender,RoutedEventArgs e)
    {
        var button=(Button)sender;button.IsEnabled=false;
        nint owner=WinRT.Interop.WindowNative.GetWindowHandle(_owner!);
        var result=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                using var dialog=new System.Windows.Forms.OpenFileDialog{Title="Выбрать программу",Filter="Программы (*.exe)|*.exe",CheckFileExists=true};
                result.SetResult(dialog.ShowDialog(new FileDialogOwner(owner))==System.Windows.Forms.DialogResult.OK?dialog.FileName:null);
            }
            catch(Exception ex){result.SetException(ex);}
        }){IsBackground=true,Name="Smoove file picker"};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        try {string? file=await result.Task;if(file is not null)AddExclusions([file]);}
        catch(Exception ex)when(ex is System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception){Connection.Message="Не удалось открыть выбор файла. Попробуйте выбрать запущенную программу.";}
        finally{button.IsEnabled=true;}
    }
    private sealed record FileDialogOwner(nint Handle):System.Windows.Forms.IWin32Window;
    private void AddExclusions(string[] paths)
    {
        foreach(string path in paths)
        {
            string normalized=ApplicationExclusion.Normalize(path);
            if(!_exclusions.Any(e=>!e.LegacyName&&string.Equals(e.Path,normalized,StringComparison.OrdinalIgnoreCase)))_exclusions.Add(new(normalized));
        }
        RenderExclusions();Apply();
    }
    private void RenderExclusions()
    {
        ExclusionCards.Children.Clear();
        foreach(var entry in _exclusions)
        {
            var row=new Grid{ColumnSpacing=12};row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            var image=new Image{Width=32,Height=32};row.Children.Add(image);if(!entry.LegacyName)_=LoadExclusionIcon(image,entry.Path);
            var text=new StackPanel{Spacing=4};text.Children.Add(new TextBlock{Text=Path.GetFileName(entry.Path),FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});text.Children.Add(new TextBlock{Text=entry.LegacyName?"Совместимое исключение • по имени файла":entry.Path,TextWrapping=TextWrapping.Wrap,FontSize=12});Grid.SetColumn(text,1);row.Children.Add(text);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};var toggle=new ToggleSwitch{IsOn=entry.Enabled,OnContent="",OffContent=""};Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle,"Включить исключение "+Path.GetFileName(entry.Path));ToolTipService.SetToolTip(toggle,"Включить исключение");
            toggle.Toggled+=(_,_)=>{int index=_exclusions.IndexOf(entry);if(index<0)index=_exclusions.FindIndex(e=>e.Path==entry.Path&&e.LegacyName==entry.LegacyName);if(index>=0)_exclusions[index]=_exclusions[index] with {Enabled=toggle.IsOn};Apply();};
            var delete=new Button{Content=new SymbolIcon(Symbol.Delete)};Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete,"Удалить исключение "+Path.GetFileName(entry.Path));ToolTipService.SetToolTip(delete,"Удалить");delete.Click+=(_,_)=>{_exclusions.RemoveAll(e=>e.Path==entry.Path&&e.LegacyName==entry.LegacyName);RenderExclusions();Apply();};actions.Children.Add(toggle);actions.Children.Add(delete);Grid.SetColumn(actions,2);row.Children.Add(actions);
            ExclusionCards.Children.Add(new Border{Child=row,Padding=new Thickness(16),CornerRadius=new CornerRadius(8),Background=(Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],BorderBrush=(Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],BorderThickness=new Thickness(1)});
        }
        if(_exclusions.Count==0)ExclusionCards.Children.Add(new TextBlock{Text="Исключений пока нет. Выберите программу одной из кнопок выше.",TextWrapping=TextWrapping.Wrap});
    }
    private static async Task LoadExclusionIcon(Image image,string path){try{image.Source=await ApplicationCatalog.Icon(path);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }}
}
