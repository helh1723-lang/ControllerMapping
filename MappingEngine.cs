using System.Diagnostics;

namespace GameControllerMapper;

public sealed class MappingEngine : IDisposable
{
    private readonly ControllerService _controllers;
    private readonly OutputRouter _outputs;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly HashSet<string> _activeSources = new(StringComparer.Ordinal);
    private MappingProfile _profile = new();
    private bool _enabled;
    private double _mouseRemainderX;
    private double _mouseRemainderY;

    public event Action<bool>? EnabledChanged;
    public event Action<InputSnapshot>? SnapshotUpdated;
    public event Action<string>? Error;

    public MappingEngine(ControllerService controllers) : this(controllers, new SendInputSink())
    {
    }

    internal MappingEngine(ControllerService controllers, IInputSink inputSink)
    {
        _controllers = controllers;
        _outputs = new OutputRouter(inputSink);
        _controllers.SelectedDeviceDisconnected += OnSelectedDeviceDisconnected;
        _loop = Task.Run(PollLoop);
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    public void SetProfile(MappingProfile profile)
    {
        lock (_gate)
        {
            _outputs.ReleaseAll();
            _activeSources.Clear();
            _profile = profile.RuntimeCopy();
            _mouseRemainderX = _mouseRemainderY = 0;
        }
    }

    public bool Enable()
    {
        if (_controllers.Selected is null)
        {
            Error?.Invoke("请先选择一个已连接的手柄。");
            return false;
        }

        lock (_gate)
        {
            if (_enabled) return true;
            _enabled = true;
        }
        EnabledChanged?.Invoke(true);
        return true;
    }

    public void Disable()
    {
        var changed = false;
        lock (_gate)
        {
            if (_enabled) changed = true;
            _enabled = false;
            _outputs.ReleaseAll();
            _activeSources.Clear();
            _mouseRemainderX = _mouseRemainderY = 0;
        }
        if (changed) EnabledChanged?.Invoke(false);
    }

    private async Task PollLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
        var stopwatch = Stopwatch.StartNew();
        var previous = stopwatch.Elapsed;
        var lastPreview = TimeSpan.Zero;

        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var now = stopwatch.Elapsed;
                var seconds = Math.Clamp((now - previous).TotalSeconds, 0, 0.05);
                previous = now;

                InputSnapshot? snapshot;
                try { snapshot = _controllers.ReadSelected(); }
                catch (Exception ex)
                {
                    Disable();
                    Error?.Invoke($"读取手柄失败：{ex.Message}");
                    continue;
                }

                if (snapshot is null)
                {
                    if (Enabled) Disable();
                    continue;
                }

                if (now - lastPreview >= TimeSpan.FromMilliseconds(40))
                {
                    lastPreview = now;
                    SnapshotUpdated?.Invoke(snapshot);
                }

                try
                {
                    lock (_gate)
                    {
                        if (_enabled) Process(snapshot, seconds);
                    }
                }
                catch (Exception ex)
                {
                    Disable();
                    Error?.Invoke($"发送映射输入失败：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Disable();
            Error?.Invoke($"映射引擎已停止：{ex.Message}");
        }
    }

    private void Process(InputSnapshot snapshot, double seconds)
    {
        foreach (var pair in _profile.Bindings)
        {
            var wasActive = _activeSources.Contains(pair.Key);
            var isActive = IsActive(pair.Key, snapshot, wasActive, _profile);
            if (isActive == wasActive) continue;

            if (isActive)
            {
                _activeSources.Add(pair.Key);
                _outputs.Acquire(pair.Value);
            }
            else
            {
                _activeSources.Remove(pair.Key);
                _outputs.Release(pair.Value);
            }
        }

        var movementX = 0d;
        var movementY = 0d;
        if (_profile.LeftStickMode == StickMode.Mouse)
            AddStickMouse(snapshot.LeftX, snapshot.LeftY, _profile, ref movementX, ref movementY);
        if (_profile.RightStickMode == StickMode.Mouse)
            AddStickMouse(snapshot.RightX, snapshot.RightY, _profile, ref movementX, ref movementY);

        _mouseRemainderX += movementX * _profile.MouseSpeed * seconds;
        _mouseRemainderY += movementY * _profile.MouseSpeed * seconds;
        var x = (int)_mouseRemainderX;
        var y = (int)_mouseRemainderY;
        _mouseRemainderX -= x;
        _mouseRemainderY -= y;
        _outputs.MoveMouse(x, y);
    }

    internal static bool IsActive(string id, InputSnapshot snapshot, bool wasActive, MappingProfile profile)
    {
        if (snapshot.Buttons.Contains(id)) return true;
        if (SourceCatalog.TryParseRaw(id, out var deviceKey, out var index))
            return deviceKey == snapshot.DeviceKey && snapshot.RawButtons.Contains(index);

        var triggerRelease = Math.Max(0, profile.TriggerThreshold - 0.10);
        if (id == "LT") return ThresholdState.Update(snapshot.LeftTrigger, wasActive, profile.TriggerThreshold, triggerRelease);
        if (id == "RT") return ThresholdState.Update(snapshot.RightTrigger, wasActive, profile.TriggerThreshold, triggerRelease);

        var directionRelease = Math.Max(0, profile.DirectionThreshold - 0.10);
        if (id.StartsWith("LS.", StringComparison.Ordinal))
        {
            if (profile.LeftStickMode != StickMode.Directions) return false;
            return DirectionActive(id[3..], snapshot.LeftX, snapshot.LeftY, wasActive, profile.DirectionThreshold, directionRelease);
        }
        if (id.StartsWith("RS.", StringComparison.Ordinal))
        {
            if (profile.RightStickMode != StickMode.Directions) return false;
            return DirectionActive(id[3..], snapshot.RightX, snapshot.RightY, wasActive, profile.DirectionThreshold, directionRelease);
        }
        return false;
    }

    private static bool DirectionActive(string direction, double x, double y, bool wasActive, double press, double release)
    {
        var value = direction switch
        {
            "Up" => y,
            "Down" => -y,
            "Left" => -x,
            "Right" => x,
            _ => 0
        };
        return ThresholdState.Update(value, wasActive, press, release);
    }

    private static void AddStickMouse(double x, double y, MappingProfile profile, ref double totalX, ref double totalY)
    {
        var magnitude = Math.Sqrt(x * x + y * y);
        if (magnitude <= profile.StickDeadzone) return;
        var scaled = Math.Clamp((magnitude - profile.StickDeadzone) / (1 - profile.StickDeadzone), 0, 1);
        totalX += x / magnitude * scaled;
        totalY -= y / magnitude * scaled;
    }

    private void OnSelectedDeviceDisconnected()
    {
        Disable();
        Error?.Invoke("手柄已断开，映射已自动关闭。");
    }

    public void Dispose()
    {
        _controllers.SelectedDeviceDisconnected -= OnSelectedDeviceDisconnected;
        Disable();
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _stop.Dispose();
    }
}

internal static class ThresholdState
{
    public static bool Update(double value, bool wasActive, double pressThreshold, double releaseThreshold) =>
        wasActive ? value >= releaseThreshold : value >= pressThreshold;
}
