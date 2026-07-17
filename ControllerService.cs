using System.Windows.Threading;

namespace GameControllerMapper;

public sealed class ControllerDevice
{
    internal nint NativePointer { get; init; }
    internal string NativeId { get; init; } = string.Empty;
    public required DeviceFingerprint Fingerprint { get; init; }
    public int RawButtonCount { get; internal set; }
    public string DisplayName => Fingerprint.DisplayName;
    public override string ToString() => DisplayName;
}

public sealed class ControllerService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dispatcher _ownerDispatcher = Dispatcher.CurrentDispatcher;
    private readonly GameInputReader _reader = new();
    private readonly Dictionary<string, int> _rawButtonCounts = new(StringComparer.Ordinal);
    private List<ControllerDevice> _devices = [];
    private ControllerDevice? _selected;
    private long _nextDeviceProbe;
    private bool _disposed;

    public event Action? DevicesChanged;
    public event Action? SelectedDeviceDisconnected;
    public string? InitializationError => _reader.InitializationError;

    public IReadOnlyList<ControllerDevice> GetDevices()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_reader.Available) return [];

            var selectedId = _selected?.NativeId;
            _selected = null;
            DisposeDevices();

            var devices = _reader.GetDevices().Select(input =>
            {
                var fingerprint = new DeviceFingerprint("Windows 游戏手柄", input.VendorId, input.ProductId, input.Id);
                return new ControllerDevice
                {
                    NativePointer = input.Pointer,
                    NativeId = input.Id,
                    Fingerprint = fingerprint,
                    RawButtonCount = _rawButtonCounts.GetValueOrDefault(fingerprint.Key)
                };
            }).ToList();

            _devices = devices;
            _selected = devices.FirstOrDefault(x => string.Equals(x.NativeId, selectedId, StringComparison.OrdinalIgnoreCase));
            return devices.ToArray();
        }
    }

    public ControllerDevice? Selected
    {
        get { lock (_gate) return _selected; }
    }

    public void Select(ControllerDevice? device)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _selected = device;
        }
    }

    public InputSnapshot? ReadSelected()
    {
        if (!_ownerDispatcher.CheckAccess())
            return _ownerDispatcher.Invoke(() => ReadSelected());

        InputSnapshot? snapshot = null;
        var devicesChanged = false;
        var disconnected = false;

        lock (_gate)
        {
            if (_disposed) return null;

            if (_selected is { } device)
            {
                if (_reader.TryRead(device.NativePointer, out var frame))
                {
                    if (device.RawButtonCount != frame.RawButtonCount)
                    {
                        device.RawButtonCount = frame.RawButtonCount;
                        _rawButtonCounts[device.Fingerprint.Key] = frame.RawButtonCount;
                        devicesChanged = true;
                    }
                    snapshot = ToSnapshot(device.Fingerprint.Key, frame);
                }
                else
                {
                    _selected = null;
                    disconnected = true;
                    devicesChanged = true;
                    _nextDeviceProbe = Environment.TickCount64 + 1000;
                }
            }
            else if (Environment.TickCount64 >= _nextDeviceProbe)
            {
                _nextDeviceProbe = Environment.TickCount64 + 1000;
                devicesChanged = true;
            }
        }

        if (disconnected) SelectedDeviceDisconnected?.Invoke();
        if (devicesChanged) DevicesChanged?.Invoke();
        return snapshot;
    }

    internal static InputSnapshot ToSnapshot(string deviceKey, GameInputFrame frame)
    {
        var buttons = new HashSet<string>(StringComparer.Ordinal);
        Add(buttons, frame.Buttons, GameInputGamepadButtons.A, "A");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.B, "B");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.X, "X");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.Y, "Y");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.LeftShoulder, "LB");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.RightShoulder, "RB");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.LeftThumbstick, "LS");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.RightThumbstick, "RS");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.DPadUp, "DPad.Up");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.DPadDown, "DPad.Down");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.DPadLeft, "DPad.Left");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.DPadRight, "DPad.Right");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.View, "View");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.Menu, "Menu");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.PaddleLeft1, "Paddle1");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.PaddleLeft2, "Paddle2");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.PaddleRight1, "Paddle3");
        Add(buttons, frame.Buttons, GameInputGamepadButtons.PaddleRight2, "Paddle4");

        return new InputSnapshot
        {
            Buttons = buttons,
            RawButtons = frame.RawButtons,
            DeviceKey = deviceKey,
            LeftTrigger = frame.LeftTrigger,
            RightTrigger = frame.RightTrigger,
            LeftX = frame.LeftX,
            LeftY = frame.LeftY,
            RightX = frame.RightX,
            RightY = frame.RightY
        };
    }

    private static void Add(HashSet<string> result, uint actual, GameInputGamepadButtons expected, string id)
    {
        if ((actual & (uint)expected) != 0) result.Add(id);
    }

    private void DisposeDevices()
    {
        foreach (var device in _devices) GameInputReader.ReleaseDevice(device.NativePointer);
        _devices = [];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _selected = null;
            DisposeDevices();
            _reader.Dispose();
        }
    }
}
