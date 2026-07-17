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
        if ((sender as FrameworkElement)?.DataContext is not MappingRow row) return;
        _engine.Disable();
        var dialog = new BindingCaptureDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Binding is null) return;
        CurrentProfile.Bindings[row.SourceId] = dialog.Binding;
        ApplyProfileChange();
    }

    private void ClearBinding_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MappingRow row) return;
        _engine.Disable();
        CurrentProfile.Bindings.Remove(row.SourceId);
        ApplyProfileChange();
    }

    private void SettingsChanged(object sender, EventArgs e)
    {
        if (_loading || !IsInitialized) return;
        var profile = CurrentProfile;
        if (LeftStickModeCombo.SelectedItem is ModeOption left) profile.LeftStickMode = left.Value;
        if (RightStickModeCombo.SelectedItem is ModeOption right) profile.RightStickMode = right.Value;
        profile.StickDeadzone = DeadzoneSlider.Value;
        profile.DirectionThreshold = DirectionSlider.Value;
        profile.TriggerThreshold = TriggerSlider.Value;
        profile.MouseSpeed = MouseSpeedSlider.Value;
        UpdateSettingLabels();
        ApplyProfileChange(false);
    }

    private void UpdateSettingLabels()
    {
        DeadzoneLabel.Text = $"摇杆死区：{DeadzoneSlider.Value:0.00}";
        DirectionLabel.Text = $"方向触发阈值：{DirectionSlider.Value:0.00}";
        TriggerLabel.Text = $"扳机触发阈值：{TriggerSlider.Value:0.00}";
        MouseSpeedLabel.Text = $"鼠标速度：{MouseSpeedSlider.Value:0} 像素/秒";
    }

    private void ApplyProfileChange(bool rebuildRows = true)
    {
        _engine.SetProfile(CurrentProfile);
        SaveProfiles();
        if (rebuildRows) BuildRows();
    }

    private void SaveProfiles()
    {
        try { _profileStore.Save(_document); }
        catch (Exception ex) { StatusText.Text = $"保存配置失败：{ex.Message}"; }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptDialog.Ask(this, "新建配置", "配置名称", $"配置 {_document.Profiles.Count + 1}");
        if (name is null) return;
        var profile = new MappingProfile { Name = name };
        _document.Profiles.Add(profile);
        _document.SelectedProfileId = profile.Id;
        LoadProfiles();
        SaveProfiles();
    }

    private void CopyProfile_Click(object sender, RoutedEventArgs e)
    {
        var copy = CurrentProfile.Clone(CurrentProfile.Name + " - 副本");
        _document.Profiles.Add(copy);
        _document.SelectedProfileId = copy.Id;
        LoadProfiles();
        SaveProfiles();
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptDialog.Ask(this, "重命名配置", "新名称", CurrentProfile.Name);
        if (name is null) return;
        CurrentProfile.Name = name;
        LoadProfiles();
        SaveProfiles();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_document.Profiles.Count == 1)
        {
            System.Windows.MessageBox.Show(this, "至少需要保留一套配置。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (System.Windows.MessageBox.Show(this, $"删除“{CurrentProfile.Name}”？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _engine.Disable();
        _document.Profiles.Remove(CurrentProfile);
        _document.SelectedProfileId = _document.Profiles[0].Id;
        LoadProfiles();
        SaveProfiles();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void OnDevicesChanged() => Dispatcher.BeginInvoke(RefreshDevices);
    private void OnEngineEnabledChanged(bool enabled) => Dispatcher.BeginInvoke(() =>
    {
        SetToggle(enabled);
        StatusText.Text = enabled ? "映射正在运行。" : "映射已关闭。";
    });

    private void OnEngineError(string message) => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = message;
        _trayIcon.BalloonTipTitle = "手柄键鼠映射";
        _trayIcon.BalloonTipText = message;
        _trayIcon.ShowBalloonTip(2500);
    });

    private void OnSnapshotUpdated(InputSnapshot snapshot) => Dispatcher.BeginInvoke(() => UpdatePreview(snapshot));

    private void UpdatePreview(InputSnapshot snapshot)
    {
        var names = SourceCatalog.Standard.Where(x => snapshot.Buttons.Contains(x.Id)).Select(x => x.DisplayName).ToList();
        if (snapshot.LeftTrigger >= CurrentProfile.TriggerThreshold) names.Add("LT 扳机");
        if (snapshot.RightTrigger >= CurrentProfile.TriggerThreshold) names.Add("RT 扳机");
        LiveButtonsText.Text = names.Count == 0 ? "无" : string.Join("、", names);
        LiveRawText.Text = snapshot.RawButtons.Count == 0 ? "无" : string.Join("、", snapshot.RawButtons.Order().Select(x => $"原始按钮 {x + 1}"));
        LiveAxesText.Text = $"左摇杆  X {snapshot.LeftX,6:0.00}  Y {snapshot.LeftY,6:0.00}    LT {snapshot.LeftTrigger:0.00}\n" +
                            $"右摇杆  X {snapshot.RightX,6:0.00}  Y {snapshot.RightY,6:0.00}    RT {snapshot.RightTrigger:0.00}";

        foreach (var row in _rows)
            row.IsActive = MappingEngine.IsActive(row.SourceId, snapshot, row.IsActive, CurrentProfile);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowMessageHook);
        if (!RegisterHotKey(_windowHandle, HotkeyId, 0x0001 | 0x0002, 0x7B))
            System.Windows.MessageBox.Show(this, "无法注册 Ctrl+Alt+F12，全局紧急关闭热键可能被其他程序占用。主界面和托盘开关仍可使用。", "热键不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam == HotkeyId)
        {
            _engine.Disable();
            handled = true;
        }
        return 0;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            _engine.Disable();
            SaveProfiles();
            if (_windowHandle != 0) UnregisterHotKey(_windowHandle, HotkeyId);
            _windowSource?.RemoveHook(WindowMessageHook);
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _engine.Dispose();
            _controllers.Dispose();
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static string FormatBinding(OutputBinding? binding)
    {
        if (binding is null) return "未设置";
        return binding.Kind switch
        {
            OutputKind.Keyboard => string.Join(" + ", binding.Keys.Select(KeyName)),
            OutputKind.MouseButton => binding.MouseButton switch
            {
                MouseButtonOutput.Left => "鼠标左键", MouseButtonOutput.Right => "鼠标右键",
                MouseButtonOutput.Middle => "鼠标中键", MouseButtonOutput.X1 => "鼠标侧键 1", _ => "鼠标侧键 2"
            },
            OutputKind.MouseWheel => binding.WheelDelta > 0 ? "滚轮向上" : "滚轮向下",
            _ => "未设置"
        };
    }

    internal static string KeyName(ushort key) => key switch
    {
        0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt", 0x5B => "Win",
        _ => KeyInterop.KeyFromVirtualKey(key).ToString()
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);

    private sealed record ModeOption(string Label, StickMode Value);
}

public sealed class MappingRow : INotifyPropertyChanged
{
    private bool _isActive;
    public string SourceId { get; }
    public string DisplayName { get; }
    public string BindingText { get; }
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; OnPropertyChanged(); }
    }

    public MappingRow(string sourceId, string displayName, string bindingText) =>
        (SourceId, DisplayName, BindingText) = (sourceId, displayName, bindingText);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
