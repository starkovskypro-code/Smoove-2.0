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
    private sealed record Values(bool Enabled, double Distance, double Acceleration, double Smooth, double Rise, double Coast, int Frequency, bool External, string Excluded, int Theme, int Version = 5, ApplicationExclusion[]? Exclusions = null, double AnimationTempo = 1,
        MotionEngineKind Engine = MotionEngineKind.Smoove, SmoothieProfile? Experimental = null, ApplicationProfile[]? Applications = null, bool OnlySelected = false);
    private readonly List<ApplicationProfile> _applications=[];
    private ScrollSettings _global = new();
    private string? _editingPath;
    private bool _editingLegacy;
    private bool _saveAllowed=true;
    private Window? _owner;
    private RunningApplicationsWindow? _picker;
    internal void SetOwner(Window owner)
    {
        if(_owner is not null && _owner!=owner) { _picker?.Close(); _picker=null; }
        _owner=owner;
    }
    public event Action? ExitRequested;
    internal void ToggleSmoothing() => Enabled.IsOn=!Enabled.IsOn;
    public MainPage(SettingsHost host)
    {
        _host = host;
        InitializeComponent();
        Defaults();
        Load();
        RefreshStartup();
        RenderApplications();
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
    private void RefreshStartup()
    {
        try
        {
            Startup.IsOn=StartupRegistration.Enabled;
            StartupMessage.Text="Автозапуск также можно отключить в настройках Windows. После перемещения папки программы включите его заново.";
        }
        catch(Exception ex) when(ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {Startup.IsEnabled=false;StartupMessage.Text="Не удалось прочитать настройку автозапуска: "+ex.Message;}
    }
    private void StartupChanged(object sender,RoutedEventArgs args)
    {
        if(!_ready)return;
        try { StartupRegistration.SetEnabled(Startup.IsOn);StartupMessage.Text=Startup.IsOn?"Автозапуск зарегистрирован для текущего пользователя. Windows может отключить его в системных настройках.":"Автозапуск выключен."; }
        catch(Exception ex) when(ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            _ready=false;RefreshStartup();_ready=true;
            StartupMessage.Text="Не удалось изменить автозапуск: "+ex.Message;
        }
    }
    private void Apply()
    {
        var tuning = ReadTuning();
        int editing = EditingIndex();
        if (editing < 0) _global = tuning;
        else _applications[editing] = _applications[editing] with { Settings = tuning };
        ProfileHint.Text = editing < 0 ? "Общий профиль. Программы со своими настройками используют отдельные значения."
            : $"Свой профиль: {_editingPath}. Частота, включение и оформление остаются общими.";
        bool experimental = EngineChoice.SelectedIndex == 1;
        SettingsColumns.Visibility = Preset.Visibility = experimental ? Visibility.Collapsed : Visibility.Visible;
        ExperimentalSettings.Visibility = experimental ? Visibility.Visible : Visibility.Collapsed;
        EngineDescription.Text = experimental
            ? "Экспериментальная модель по формулам Smoothie Scroll 1.4.2. После паузы обратный щелчок плавно тормозит хвост; инерция может увеличивать путь."
            : "Наш движок: мягкий разгон, разворот и торможение. При смене движка текущая прокрутка останавливается. Настройки каждого сохраняются отдельно.";
        ExperimentalDistanceValue.Text = $"{ExperimentalDistance.Value:F1}×";
        ExperimentalSmoothnessValue.Text = $"{ExperimentalSmoothness.Value:F0} мс";
        ExperimentalAccelerationValue.Text = $"{ExperimentalAcceleration.Value:F1}";
        ExperimentalInertiaValue.Text = $"{ExperimentalInertia.Value:F1}";
        DistanceValue.Text = $"{Distance.Value:F1}×";
        AccelerationValue.Text = $"{Acceleration.Value:F0}%";
        SmoothValue.Text = $"{Smooth.Value:F0}%";
        AnimationTempoValue.Text = $"{AnimationTempo.Value:F1}×";
        RiseValue.Text = $"{Rise.Value:F0} мс";
        CoastValue.Text = $"{Coast.Value:F0} мс";
        int hz = int.Parse((string)((ComboBoxItem)Frequency.SelectedItem).Tag);
        _host.Configure(Enabled.IsOn, _global.MotionProfile.RiseSeconds, _global.MotionProfile.CoastSeconds,
            _global.Distance, _global.Acceleration / 100, hz, External.IsOn ? "" : "*", _global.AnimationTempo,
            _global.Engine, _global.Experimental ?? new());
        _host.SetApplicationProfiles(_applications.ToArray(), AppScope.SelectedIndex == 1);
        Connection.Title = Enabled.IsOn ? "Smoove работает" : "Прокрутка на паузе";
        Connection.Severity = Enabled.IsOn ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        try
        {
            if(!_saveAllowed)return;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Values(Enabled.IsOn, _global.Distance, _global.Acceleration, _global.Smooth, _global.Rise, _global.Coast, Frequency.SelectedIndex, External.IsOn, "", ThemeChoice.SelectedIndex,5,null,_global.AnimationTempo,
                _global.Engine, _global.Experimental, _applications.ToArray(), AppScope.SelectedIndex == 1)));
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
                !double.IsFinite(v.Distance+v.Acceleration+v.Smooth+v.Rise+v.Coast) ||
                !double.IsFinite(v.AnimationTempo) || v.AnimationTempo is < 0.5 or > 8 || !Enum.IsDefined(v.Engine)) { _saveAllowed=false; return; }
            var experimental = v.Experimental ?? new SmoothieProfile();
            experimental.Validate();
            if(v.Version>5){_saveAllowed=false;Connection.Message="Настройки созданы более новой версией. Файл не будет перезаписан.";}
            var profiles = (v.Applications ?? ApplicationProfile.Migrate(v.Exclusions ?? ApplicationExclusion.Migrate(v.Excluded)))
                .Select(p => p?.Validated() ?? throw new JsonException("Некорректная запись программы.")).ToArray();
            foreach(var validated in profiles)
            {
                if(!_applications.Any(e=>e.LegacyName==validated.LegacyName&&string.Equals(e.Path,validated.Path,StringComparison.OrdinalIgnoreCase)))_applications.Add(validated);
            }
            Enabled.IsOn=v.Enabled; Distance.Value=Math.Clamp(v.Distance,0.5,4); Acceleration.Value=Math.Clamp(v.Acceleration,0,100);
            Smooth.Value=Math.Clamp(v.Smooth,50,200); Rise.Value=Math.Clamp(v.Rise,5,300); Coast.Value=Math.Clamp(v.Coast,5,500);
            AnimationTempo.Value=v.AnimationTempo;
            EngineChoice.SelectedIndex=(int)v.Engine;
            ExperimentalDistance.Value=experimental.DistancePerNotch; ExperimentalSmoothness.Value=experimental.SmoothnessMs;
            ExperimentalAcceleration.Value=experimental.Acceleration; ExperimentalInertia.Value=experimental.Inertia;
            Frequency.SelectedIndex=Math.Clamp(v.Frequency,0,4); External.IsOn=v.External; ThemeChoice.SelectedIndex=Math.Clamp(v.Theme,0,2);
            AppScope.SelectedIndex = v.OnlySelected ? 1 : 0;
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
        _ready=false; WriteTuning(new()); _ready=true; Apply();
    }
    private void Defaults()
    {
        Distance.Value=1; Acceleration.Value=60; Smooth.Value=110; Rise.Value=70; Coast.Value=160;
        AnimationTempo.Value=1;
        EngineChoice.SelectedIndex=0;
        ExperimentalDistance.Value=8; ExperimentalSmoothness.Value=80;
        ExperimentalAcceleration.Value=1; ExperimentalInertia.Value=0.5;
        Frequency.SelectedIndex=4; Enabled.IsOn=true; External.IsOn=true; Preset.SelectedIndex=0;
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
        ResetSettingsButton.Visibility=tag=="scroll"?Visibility.Visible:Visibility.Collapsed;
        TitleText.Text=tag switch {"apps"=>"Программы","diagnostics"=>"Диагностика",_=>"Настройки"};
        SubtitleText.Text=tag switch {"apps"=>"Профили и исключения для отдельных программ.","diagnostics"=>"Состояние обработки и технические счётчики.",_=>"Настройте движение под свой ритм. Изменения применяются сразу."};
    }
    private void ExitClick(object sender,RoutedEventArgs e) { _timer.Stop(); ExitRequested?.Invoke(); }
    private void ChooseRunning(object sender,RoutedEventArgs e)
    {
        if(_owner is null)return;
        if(_picker is not null){_picker.Activate();return;}
        _picker=new(_owner,AddApplications);_picker.Closed+=(_,_)=>_picker=null;_picker.Activate();
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
        try {string? file=await result.Task;if(file is not null)AddApplications([file]);}
        catch(Exception ex)when(ex is System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception){Connection.Message="Не удалось открыть выбор файла. Попробуйте выбрать запущенную программу.";}
        finally{button.IsEnabled=true;}
    }
    private sealed record FileDialogOwner(nint Handle):System.Windows.Forms.IWin32Window;
    private ScrollSettings ReadTuning() => new(Distance.Value, Acceleration.Value, Smooth.Value, Rise.Value, Coast.Value,
        AnimationTempo.Value, (MotionEngineKind)EngineChoice.SelectedIndex,
        new(ExperimentalDistance.Value, ExperimentalSmoothness.Value, ExperimentalAcceleration.Value, ExperimentalInertia.Value));
    private void WriteTuning(ScrollSettings settings)
    {
        Distance.Value=settings.Distance; Acceleration.Value=settings.Acceleration; Smooth.Value=settings.Smooth;
        Rise.Value=settings.Rise; Coast.Value=settings.Coast; AnimationTempo.Value=settings.AnimationTempo;
        EngineChoice.SelectedIndex=(int)settings.Engine;
        var experimental=settings.Experimental ?? new();
        ExperimentalDistance.Value=experimental.DistancePerNotch; ExperimentalSmoothness.Value=experimental.SmoothnessMs;
        ExperimentalAcceleration.Value=experimental.Acceleration; ExperimentalInertia.Value=experimental.Inertia;
        Preset.SelectedIndex=settings.Rise==110 && settings.Coast==280 ? 1 : 0;
    }
    private int EditingIndex() => _editingPath is null ? -1 : _applications.FindIndex(p => p.Path == _editingPath && p.LegacyName == _editingLegacy && p.Mode == ApplicationScrollMode.Custom);
    private void ProfileScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ProfileScope.SelectedItem is not ComboBoxItem item) return;
        _ready=false;
        var tagged=item.Tag as ApplicationProfile;
        var profile=tagged is null ? null : _applications.FirstOrDefault(p => p.Path==tagged.Path && p.LegacyName==tagged.LegacyName);
        _editingPath=profile?.Path; _editingLegacy=profile?.LegacyName ?? false;
        WriteTuning(profile?.Settings ?? _global);
        _ready=true; Apply();
    }
    private void RenderProfileScope()
    {
        bool ready=_ready; _ready=false;
        ProfileScope.Items.Clear();
        ProfileScope.Items.Add(new ComboBoxItem { Content="Общие настройки" });
        int selection=0;
        foreach (var profile in _applications.Where(p => p.Mode == ApplicationScrollMode.Custom))
        {
            ProfileScope.Items.Add(new ComboBoxItem { Content=Path.GetFileName(profile.Path), Tag=profile });
            if (profile.Path == _editingPath && profile.LegacyName == _editingLegacy) selection=ProfileScope.Items.Count-1;
        }
        if (selection==0 && _editingPath is not null) { _editingPath=null; WriteTuning(_global); }
        ProfileScope.SelectedIndex=selection;
        _ready=ready;
    }
    private void AddApplications(string[] paths)
    {
        foreach(string path in paths)
        {
            string normalized=ApplicationExclusion.Normalize(path);
            if(!_applications.Any(e=>!e.LegacyName&&string.Equals(e.Path,normalized,StringComparison.OrdinalIgnoreCase)))_applications.Add(new(normalized));
        }
        RenderApplications();Apply();
    }
    private void RenderApplications()
    {
        RenderProfileScope();
        ExclusionCards.Children.Clear();
        foreach(var entry in _applications)
        {
            string name=Path.GetFileName(entry.Path);
            int Index() => _applications.FindIndex(e=>e.Path==entry.Path&&e.LegacyName==entry.LegacyName);
            var card=new StackPanel { Spacing=12 };
            var row=new Grid { ColumnSpacing=12 };
            row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            var image=new Image{Width=32,Height=32};row.Children.Add(image);if(!entry.LegacyName)_=LoadApplicationIcon(image,entry.Path);
            var text=new StackPanel{Spacing=4};text.Children.Add(new TextBlock{Text=name,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            text.Children.Add(new TextBlock{Text=entry.LegacyName?"Совместимая запись • по имени файла":entry.Path,TextWrapping=TextWrapping.Wrap,FontSize=12});Grid.SetColumn(text,1);row.Children.Add(text);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12,VerticalAlignment=VerticalAlignment.Center};
            var toggle=new ToggleSwitch{IsOn=entry.Enabled,OnContent="",OffContent="",MinWidth=0,Width=40};
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle,"Включить профиль "+name);
            ToolTipService.SetToolTip(toggle,"Использовать запись программы");
            toggle.Toggled+=(_,_)=>{int index=Index();if(index>=0)_applications[index]=_applications[index] with {Enabled=toggle.IsOn};Apply();};
            var delete=new Button{Content=new FontIcon{Glyph="\uE74D",FontSize=18},Style=(Style)Application.Current.Resources["DeleteExclusionButton"]};
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete,"Удалить профиль "+name);ToolTipService.SetToolTip(delete,"Удалить");
            delete.Click+=(_,_)=>{_applications.RemoveAll(e=>e.Path==entry.Path&&e.LegacyName==entry.LegacyName);RenderApplications();Apply();};
            actions.Children.Add(toggle);actions.Children.Add(delete);Grid.SetColumn(actions,2);row.Children.Add(actions);card.Children.Add(row);
            var choices=new Grid { ColumnSpacing=12 };
            choices.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});choices.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            var mode=new ComboBox { HorizontalAlignment=HorizontalAlignment.Stretch };
            foreach(string label in new[]{"Общие настройки","Свой профиль","Исключить — стандартное колесо"})mode.Items.Add(new ComboBoxItem{Content=label});
            mode.SelectedIndex=(int)entry.Mode;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mode,"Режим "+name);
            var edit=new Button { Content="Настроить", IsEnabled=entry.Mode==ApplicationScrollMode.Custom };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit,"Настроить "+name);
            mode.SelectionChanged+=(_,_)=>
            {
                int index=Index();if(index<0)return;
                var next=(ApplicationScrollMode)mode.SelectedIndex;
                _applications[index]=_applications[index] with {Mode=next,Settings=_applications[index].Settings ?? (next==ApplicationScrollMode.Custom ? _global : null)};
                edit.IsEnabled=next==ApplicationScrollMode.Custom;
                RenderProfileScope();Apply();
            };
            edit.Click+=(_,_)=>
            {
                int index=Index();if(index<0)return;
                for(int i=1;i<ProfileScope.Items.Count;i++)
                    if(((ComboBoxItem)ProfileScope.Items[i]).Tag is ApplicationProfile p && p.Path==entry.Path && p.LegacyName==entry.LegacyName){ProfileScope.SelectedIndex=i;break;}
                Nav.SelectedItem=Nav.MenuItems[0];
            };
            choices.Children.Add(mode);Grid.SetColumn(edit,1);choices.Children.Add(edit);card.Children.Add(choices);
            ExclusionCards.Children.Add(new Border{Child=card,Style=(Style)Resources["ApplicationProfileCard"]});
        }
        if(_applications.Count==0)ExclusionCards.Children.Add(new TextBlock{Text="Программ пока нет. Добавьте программу кнопкой выше.",TextWrapping=TextWrapping.Wrap});
    }
    private static async Task LoadApplicationIcon(Image image,string path){try{image.Source=await ApplicationCatalog.Icon(path);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }}
}
