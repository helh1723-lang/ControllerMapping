using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace GameControllerMapper;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x4D47;
    private const int WmHotkey = 0x0312;
    private readonly ProfileStore _profileStore = new();
    private readonly ControllerService _controllers = new();
    private readonly MappingEngine _engine;
    private readonly ObservableCollection<MappingRow> _rows = [];
    private readonly WinForms.NotifyIcon _trayIcon;
    private readonly WinForms.ToolStripMenuItem _trayToggle;
    private ProfileDocument _document;
    private bool _loading;
    private bool _syncingToggle;
    private bool _refreshingDevices;
    private nint _windowHandle;
    private HwndSource? _windowSource;

    private MappingProfile CurrentProfile =>
        ProfileCombo.SelectedItem as MappingProfile ?? _document.Profiles[0];

    public MainWindow()
    {
        InitializeComponent();
        _document = _profileStore.Load();
        _engine = new MappingEngine(_controllers);
        MappingGrid.ItemsSource = _rows;

        var modes = new[]
        {
            new ModeOption("四方向按键", StickMode.Directions),
            new ModeOption("鼠标移动", StickMode.Mouse),
            new ModeOption("禁用", StickMode.Disabled)
        };
        LeftStickModeCombo.ItemsSource = modes;
        RightStickModeCombo.ItemsSource = modes;

        _trayToggle = new WinForms.ToolStripMenuItem("启用映射", null, (_, _) => Dispatcher.Invoke(ToggleFromTray));
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "手柄键鼠映射",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        _controllers.DevicesChanged += OnDevicesChanged;
        _engine.EnabledChanged += OnEngineEnabledChanged;
        _engine.SnapshotUpdated += OnSnapshotUpdated;
        _engine.Error += OnEngineError;

        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;

        LoadProfiles();
        RefreshDevices();
    }

    private void LoadProfiles()
    {
        _loading = true;
        ProfileCombo.ItemsSource = null;
        ProfileCombo.ItemsSource = _document.Profiles;
        ProfileCombo.SelectedItem = _document.Profiles.FirstOrDefault(x => x.Id == _document.SelectedProfileId)
                                    ?? _document.Profiles[0];
        _loading = false;
        LoadCurrentProfile();
    }

    private void LoadCurrentProfile()
    {
        _loading = true;
        var profile = CurrentProfile;
        LeftStickModeCombo.SelectedItem = ((IEnumerable<ModeOption>)LeftStickModeCombo.ItemsSource).First(x => x.Value == profile.LeftStickMode);
        RightStickModeCombo.SelectedItem = ((IEnumerable<ModeOption>)RightStickModeCombo.ItemsSource).First(x => x.Value == profile.RightStickMode);
        DeadzoneSlider.Value = profile.StickDeadzone;
        DirectionSlider.Value = profile.DirectionThreshold;
        TriggerSlider.Value = profile.TriggerThreshold;
        MouseSpeedSlider.Value = profile.MouseSpeed;
        UpdateSettingLabels();
        _loading = false;
        _engine.SetProfile(profile);
        BuildRows();
    }

    private void RefreshDevices()
    {
        _refreshingDevices = true;
        var previousKey = _controllers.Selected?.Fingerprint.Key;
        var devices = _controllers.GetDevices();
        DeviceCombo.ItemsSource = devices;
        var selected = devices.FirstOrDefault(x => x.Fingerprint.Key == previousKey) ?? devices.FirstOrDefault();
        DeviceCombo.SelectedItem = selected;
        _controllers.Select(selected);
        _refreshingDevices = false;
        BuildRows();
        StatusText.Text = selected is null
            ? _controllers.InitializationError ?? "未检测到手柄。请先在 Windows 中完成有线连接或蓝牙配对。"
            : HasLegacyRawBindings(selected)
                ? $"已连接：{selected.DisplayName}。输入后端已升级，请重新绑定此手柄的原始按钮；旧绑定已保留。"
                : $"已连接：{selected.DisplayName}（原始按钮 {selected.RawButtonCount} 个）";
    }

    private bool HasLegacyRawBindings(ControllerDevice device)
    {
        var prefix = $"{device.Fingerprint.VendorId:X4}:{device.Fingerprint.ProductId:X4}:";
        return _document.Profiles
            .SelectMany(profile => profile.Bindings.Keys)
            .Any(id => SourceCatalog.TryParseRaw(id, out var key, out _) &&
                       key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                       !key.Equals(device.Fingerprint.Key, StringComparison.OrdinalIgnoreCase));
    }

    private void BuildRows()
    {
        if (!IsInitialized) return;
        var profile = CurrentProfile;
        _rows.Clear();
        foreach (var source in SourceCatalog.Standard)
            _rows.Add(new MappingRow(source.Id, source.DisplayName, FormatBinding(profile.Bindings.GetValueOrDefault(source.Id))));

        if (_controllers.Selected is { } device)
        {
            for (var i = 0; i < device.RawButtonCount; i++)
            {
                var id = SourceCatalog.RawId(device.Fingerprint.Key, i);
                _rows.Add(new MappingRow(id, $"原始按钮 {i + 1}", FormatBinding(profile.Bindings.GetValueOrDefault(id))));
            }
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingDevices) return;
        _engine.Disable();
        _controllers.Select(DeviceCombo.SelectedItem as ControllerDevice);
        BuildRows();
        StatusText.Text = _controllers.Selected is { } device ? $"已选择：{device.DisplayName}" : "未选择手柄";
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileCombo.SelectedItem is not MappingProfile profile) return;
        _engine.Disable();
        _document.SelectedProfileId = profile.Id;
        SaveProfiles();
        LoadCurrentProfile();
    }

    private void MappingToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle) return;
        if (MappingToggle.IsChecked == true)
        {
            _engine.SetProfile(CurrentProfile);
            if (!_engine.Enable()) SetToggle(false);
        }
        else _engine.Disable();
    }

    private void ToggleFromTray()
    {
        if (_engine.Enabled) _engine.Disable();
        else
        {
            _engine.SetProfile(CurrentProfile);
            _engine.Enable();
        }
    }

    private void SetToggle(bool enabled)
    {
        _syncingToggle = true;
        MappingToggle.IsChecked = enabled;
        MappingToggle.Content = enabled ? "映射已启用" : "映射已关闭";
        MappingToggle.Background = enabled ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 180, 105)) : null;
        MappingToggle.Foreground = enabled ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
        _trayToggle.Text = enabled ? "停用映射" : "启用映射";
        _syncingToggle = false;
    }

    private void EditBinding_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MappingRow row) return;…12691 tokens truncated…ateDirectory(directory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, _json));
        File.Move(temporary, FilePath, true);
    }

    public string Serialize(ProfileDocument document) => JsonSerializer.Serialize(document, _json);
    public ProfileDocument? Deserialize(string json) => JsonSerializer.Deserialize<ProfileDocument>(json, _json);

    private static void Normalize(ProfileDocument document)
    {
        if (document.Profiles.Count == 0) document.Profiles.Add(new MappingProfile());
        foreach (var profile in document.Profiles)
        {
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "未命名配置" : profile.Name.Trim();
            profile.Bindings ??= new(StringComparer.Ordinal);
            profile.StickDeadzone = Math.Clamp(profile.StickDeadzone, 0, 0.9);
            profile.DirectionThreshold = Math.Clamp(profile.DirectionThreshold, 0.1, 1);
            profile.TriggerThreshold = Math.Clamp(profile.TriggerThreshold, 0.05, 1);
            profile.MouseSpeed = Math.Clamp(profile.MouseSpeed, 50, 5000);
        }

        if (document.Profiles.All(x => x.Id != document.SelectedProfileId))
            document.SelectedProfileId = document.Profiles[0].Id;
    }
}
