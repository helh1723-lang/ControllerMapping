using System.IO;
using System.Windows.Threading;

namespace GameControllerMapper;

internal static class SelfTest
{
    public static int Run()
    {
        try
        {
            TestReferenceCountingAndChordOrder();
            TestHysteresis();
            TestGameInputSnapshotMapping();
            TestImeKeyResolution();
            TestProfileRoundTrip();
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    public static int RunNativeInput()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "native-input-test.txt");
        var canReadCursor = GetCursorPos(out var before);
        var width = GetSystemMetrics(0);
        var offset = !canReadCursor ? 0 : before.X < width - 20 ? 12 : -12;
        try
        {
            new SendInputSink().MouseMove(offset, 0);
            Thread.Sleep(100);
            if (!canReadCursor)
            {
                File.WriteAllText(path, "SendInput accepted; cursor position unavailable in this desktop session");
                return 0;
            }

            if (!GetCursorPos(out var after))
            {
                File.WriteAllText(path, "SendInput accepted; GetCursorPos(after) failed");
                return 0;
            }

            File.WriteAllText(path, $"before={before.X},{before.Y}|after={after.X},{after.Y}|offset={offset}");
            return after.X != before.X ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, ex.ToString());
            return 1;
        }
        finally
        {
            if (canReadCursor) SetCursorPos(before.X, before.Y);
        }
    }

    public static int RunGameInput()
    {
        using var reader = new GameInputReader();
        return reader.Available ? 0 : 1;
    }

    public static int RunGameInputDevice()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "gameinput-device-test.txt");
        using var controllers = new ControllerService();
        var lines = new List<string>();
        var succeeded = true;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var devices = controllers.GetDevices();
            lines.Add($"cycle={cycle + 1}|devices={devices.Count}");
            var device = devices.FirstOrDefault();
            if (device is null) { succeeded = false; break; }
            controllers.Select(device);
            var reading = false;
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    reading |= controllers.ReadSelected() is not null;
                    Thread.Sleep(8);
                }
            }
            catch (Exception ex)
            {
                succeeded = false;
                lines.Add(ex.ToString());
            }
            lines.Add($"{device.DisplayName}|{device.Fingerprint.Key}|reading={reading}");
            succeeded &= reading;
        }
        File.WriteAllLines(path, lines);
        return succeeded ? 0 : 1;
    }

    public static int RunGameInputThread()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "gameinput-thread-test.txt");
        using var controllers = new ControllerService();
        var device = controllers.GetDevices().FirstOrDefault();
        if (device is null) return 1;
        controllers.Select(device);
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var task = Task.Run(() => Enumerable.Range(0, 100)
                .Any(_ => controllers.ReadSelected() is not null));
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            var succeeded = task.GetAwaiter().GetResult();
            File.WriteAllText(path, $"reading={succeeded}");
            return succeeded ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, ex.ToString());
            return 1;
        }
    }

    private static void TestReferenceCountingAndChordOrder()
    {
        var sink = new RecordingSink();
        var router = new OutputRouter(sink);
        var chord = new OutputBinding { Kind = OutputKind.Keyboard, Keys = [0x11, 0x10, 0x4B] };

        router.Acquire(chord);
        router.Acquire(chord);
        Require(sink.Events.SequenceEqual(["KD:17", "KD:16", "KD:75"]));
        router.Release(chord);
        Require(sink.Events.Count == 3);
        router.Release(chord);
        Require(sink.Events.SequenceEqual(["KD:17", "KD:16", "KD:75", "KU:75", "KU:16", "KU:17"]));
    }

    private static void TestHysteresis()
    {
        Require(!ThresholdState.Update(0.54, false, 0.55, 0.45));
        Require(ThresholdState.Update(0.56, false, 0.55, 0.45));
        Require(ThresholdState.Update(0.46, true, 0.55, 0.45));
        Require(!ThresholdState.Update(0.44, true, 0.55, 0.45));
    }

    private static void TestGameInputSnapshotMapping()
    {
        var buttons = GameInputGamepadButtons.A |
                      GameInputGamepadButtons.DPadLeft |
                      GameInputGamepadButtons.PaddleLeft1;
        var frame = new GameInputFrame((uint)buttons, 0.6f, 0.7f, 0.1f, -0.2f, 0.3f, -0.4f, 3, [1, 2]);
        var snapshot = ControllerService.ToSnapshot("device", frame);

        Require(GameInputReader.EnableBackgroundInput == 0x00000040);
        Require(snapshot.Buttons.SetEquals(["A", "DPad.Left", "Paddle1"]));
        Require(snapshot.RawButtons.SetEquals([1, 2]));
        Require(snapshot.DeviceKey == "device");
        Require(Math.Abs(snapshot.LeftTrigger - 0.6) < 0.001);
        Require(Math.Abs(snapshot.RightY + 0.4) < 0.001);
    }

    private static void TestProfileRoundTrip()
    {
        var profile = new MappingProfile { Name = "测试", MouseSpeed = 777 };
        profile.Bindings["A"] = new OutputBinding { Kind = OutputKind.Keyboard, Keys = [0x11, 0x43] };
        var document = new ProfileDocument { SelectedProfileId = profile.Id, Profiles = [profile] };
        var store = new ProfileStore();
        var loaded = store.Deserialize(store.Serialize(document));
        Require(loaded?.Profiles.Single().Bindings["A"].Keys.SequenceEqual(new ushort[] { 0x11, 0x43 }) == true);
        Require(loaded!.Profiles.Single().MouseSpeed == 777);
    }

    private static void TestImeKeyResolution()
    {
        var resolved = BindingKeyResolver.Resolve(
            System.Windows.Input.Key.ImeProcessed,
            System.Windows.Input.Key.None,
            System.Windows.Input.Key.J,
            System.Windows.Input.Key.None);
        Require(resolved == System.Windows.Input.Key.J);
        Require(System.Windows.Input.KeyInterop.VirtualKeyFromKey(resolved) == 0x4A);
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Self-test failed");
    }

    private sealed class RecordingSink : IInputSink
    {
        public List<string> Events { get; } = [];
        public void KeyDown(ushort virtualKey) => Events.Add($"KD:{virtualKey}");
        public void KeyUp(ushort virtualKey) => Events.Add($"KU:{virtualKey}");
        public void MouseDown(MouseButtonOutput button) => Events.Add($"MD:{button}");
        public void MouseUp(MouseButtonOutput button) => Events.Add($"MU:{button}");
        public void MouseWheel(int delta) => Events.Add($"MW:{delta}");
        public void MouseMove(int x, int y) => Events.Add($"MM:{x},{y}");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
