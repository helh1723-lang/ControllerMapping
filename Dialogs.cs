using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfButton = System.Windows.Controls.Button;

namespace GameControllerMapper;

internal sealed class BindingCaptureDialog : Window
{
    private readonly TextBlock _prompt;
    private Key? _pendingModifier;
    public OutputBinding? Binding { get; private set; }

    public BindingCaptureDialog()
    {
        Title = "设置映射输出";
        Width = 520;
        Height = 330;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "按下一个键或组合键", FontSize = 20, FontWeight = FontWeights.SemiBold });
        _prompt = new TextBlock
        {
            Text = "例如 Ctrl + Shift + K。按 Esc 取消。",
            Margin = new Thickness(0, 8, 0, 18),
            Foreground = System.Windows.Media.Brushes.DimGray
        };
        root.Children.Add(_prompt);
        root.Children.Add(new TextBlock { Text = "或选择鼠标动作：", Margin = new Thickness(0, 4, 0, 8) });

        var mouse = new WrapPanel();
        AddMouseButton(mouse, "左键", MouseButtonOutput.Left);
        AddMouseButton(mouse, "右键", MouseButtonOutput.Right);
        AddMouseButton(mouse, "中键", MouseButtonOutput.Middle);
        AddMouseButton(mouse, "侧键 1", MouseButtonOutput.X1);
        AddMouseButton(mouse, "侧键 2", MouseButtonOutput.X2);
        AddWheelButton(mouse, "滚轮向上", 120);
        AddWheelButton(mouse, "滚轮向下", -120);
        root.Children.Add(mouse);
        root.Children.Add(new WpfButton { Content = "取消", Margin = new Thickness(0, 22, 0, 0), Width = 90, IsCancel = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right });
        Content = root;

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Loaded += (_, _) => Focus();
    }

    private void AddMouseButton(System.Windows.Controls.Panel panel, string text, MouseButtonOutput button)
    {
        var control = new WpfButton { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 6, 10, 6) };
        control.Click += (_, _) => Finish(new OutputBinding { Kind = OutputKind.MouseButton, MouseButton = button });
        panel.Children.Add(control);
    }

    private void AddWheelButton(System.Windows.Controls.Panel panel, string text, int delta)
    {
        var control = new WpfButton { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 6, 10, 6) };
        control.Click += (_, _) => Finish(new OutputBinding { Kind = OutputKind.MouseWheel, WheelDelta = delta });
        panel.Children.Add(control);
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = BindingKeyResolver.Resolve(e.Key, e.SystemKey, e.ImeProcessedKey, e.DeadCharProcessedKey);
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        if (IsModifier(key))
        {
            _pendingModifier = key;
            _prompt.Text = $"已按下 {key}，继续按主键；单独释放可只映射此键。";
            e.Handled = true;
            return;
        }

        var keys = ModifierVirtualKeys(Keyboard.Modifiers);
        var mainKey = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (mainKey != 0 && !keys.Contains(mainKey)) keys.Add(mainKey);
        if (keys.Count > 0) Finish(new OutputBinding { Kind = OutputKind.Keyboard, Keys = keys });
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = BindingKeyResolver.Resolve(e.Key, e.SystemKey, e.ImeProcessedKey, e.DeadCharProcessedKey);
        if (_pendingModifier != key) return;
        var virtualKey = ModifierVirtualKey(key);
        if (virtualKey != 0) Finish(new OutputBinding { Kind = OutputKind.Keyboard, Keys = [virtualKey] });
        e.Handled = true;
    }

    private void Finish(OutputBinding binding)
    {
        Binding = binding;
        DialogResult = true;
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    private static ushort ModifierVirtualKey(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => 0x11,
        Key.LeftShift or Key.RightShift => 0x10,
        Key.LeftAlt or Key.RightAlt => 0x12,
        Key.LWin or Key.RWin => 0x5B,
        _ => 0
    };

    private static List<ushort> ModifierVirtualKeys(ModifierKeys modifiers)
    {
        var result = new List<ushort>();
        if (modifiers.HasFlag(ModifierKeys.Control)) result.Add(0x11);
        if (modifiers.HasFlag(ModifierKeys.Shift)) result.Add(0x10);
        if (modifiers.HasFlag(ModifierKeys.Alt)) result.Add(0x12);
        if (modifiers.HasFlag(ModifierKeys.Windows)) result.Add(0x5B);
        return result;
    }
}

internal static class BindingKeyResolver
{
    public static Key Resolve(Key key, Key systemKey, Key imeProcessedKey, Key deadCharProcessedKey) => key switch
    {
        Key.System => systemKey,
        Key.ImeProcessed => imeProcessedKey,
        Key.DeadCharProcessed => deadCharProcessedKey,
        _ => key
    };
}

internal sealed class TextPromptDialog : Window
{
    private readonly System.Windows.Controls.TextBox _text;

    private TextPromptDialog(string title, string label, string value)
    {
        Title = title;
        Width = 380;
        Height = 180;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = label });
        _text = new System.Windows.Controls.TextBox { Text = value, Margin = new Thickness(0, 8, 0, 16), Padding = new Thickness(6) };
        root.Children.Add(_text);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(new WpfButton { Content = "取消", Width = 80, IsCancel = true, Margin = new Thickness(4) });
        var ok = new WpfButton { Content = "确定", Width = 80, IsDefault = true, Margin = new Thickness(4) };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(_text.Text)) DialogResult = true; };
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { _text.SelectAll(); _text.Focus(); };
    }

    public static string? Ask(Window owner, string title, string label, string value)
    {
        var dialog = new TextPromptDialog(title, label, value) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._text.Text.Trim() : null;
    }
}
