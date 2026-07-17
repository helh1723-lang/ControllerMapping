using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GameControllerMapper;

internal interface IInputSink
{
    void KeyDown(ushort virtualKey);
    void KeyUp(ushort virtualKey);
    void MouseDown(MouseButtonOutput button);
    void MouseUp(MouseButtonOutput button);
    void MouseWheel(int delta);
    void MouseMove(int x, int y);
}

internal sealed class SendInputSink : IInputSink
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyUpFlag = 0x0002;
    private const uint MouseMoveFlag = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint MouseWheelFlag = 0x0800;

    public void KeyDown(ushort virtualKey) => SendKeyboard(virtualKey, false);
    public void KeyUp(ushort virtualKey) => SendKeyboard(virtualKey, true);
    public void MouseDown(MouseButtonOutput button) => SendMouse(ButtonFlag(button, false), 0, data: ButtonData(button));
    public void MouseUp(MouseButtonOutput button) => SendMouse(ButtonFlag(button, true), 0, data: ButtonData(button));
    public void MouseWheel(int delta) => SendMouse(MouseWheelFlag, 0, data: unchecked((uint)delta));
    public void MouseMove(int x, int y) => SendMouse(MouseMoveFlag, x, y: y);

    private static void SendKeyboard(ushort key, bool up)
    {
        var input = new NativeInput
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? KeyUpFlag : 0 } }
        };
        Send(input);
    }

    private static void SendMouse(uint flags, int x, int y = 0, uint data = 0)
    {
        var input = new NativeInput
        {
            Type = InputMouse,
            Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, MouseData = data, Flags = flags } }
        };
        Send(input);
    }

    private static void Send(NativeInput input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 拒绝发送模拟输入");
    }

    private static uint ButtonFlag(MouseButtonOutput button, bool up) => (button, up) switch
    {
        (MouseButtonOutput.Left, false) => MouseLeftDown,
        (MouseButtonOutput.Left, true) => MouseLeftUp,
        (MouseButtonOutput.Right, false) => MouseRightDown,
        (MouseButtonOutput.Right, true) => MouseRightUp,
        (MouseButtonOutput.Middle, false) => MouseMiddleDown,
        (MouseButtonOutput.Middle, true) => MouseMiddleUp,
        (_, false) => MouseXDown,
        _ => MouseXUp
    };

    private static uint ButtonData(MouseButtonOutput button) => button switch
    {
        MouseButtonOutput.X1 => 1,
        MouseButtonOutput.X2 => 2,
        _ => 0
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, NativeInput[] inputs, int sizeOfInputStructure);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}

internal sealed class OutputRouter
{
    private readonly IInputSink _sink;
    private readonly Dictionary<ushort, int> _keyReferences = [];
    private readonly Dictionary<MouseButtonOutput, int> _mouseReferences = [];

    public OutputRouter(IInputSink sink) => _sink = sink;

    public void Acquire(OutputBinding binding)
    {
        if (binding.Kind == OutputKind.Keyboard)
        {
            foreach (var key in binding.Keys.Distinct())
            {
                var count = _keyReferences.GetValueOrDefault(key);
                _keyReferences[key] = count + 1;
                if (count == 0) _sink.KeyDown(key);
            }
        }
        else if (binding.Kind == OutputKind.MouseButton)
        {
            var count = _mouseReferences.GetValueOrDefault(binding.MouseButton);
            _mouseReferences[binding.MouseButton] = count + 1;
            if (count == 0) _sink.MouseDown(binding.MouseButton);
        }
        else
        {
            _sink.MouseWheel(binding.WheelDelta);
        }
    }

    public void Release(OutputBinding binding)
    {
        if (binding.Kind == OutputKind.Keyboard)
        {
            foreach (var key in binding.Keys.Distinct().Reverse())
            {
                if (!_keyReferences.TryGetValue(key, out var count)) continue;
                if (count <= 1)
                {
                    _keyReferences.Remove(key);
                    _sink.KeyUp(key);
                }
                else _keyReferences[key] = count - 1;
            }
        }
        else if (binding.Kind == OutputKind.MouseButton && _mouseReferences.TryGetValue(binding.MouseButton, out var count))
        {
            if (count <= 1)
            {
                _mouseReferences.Remove(binding.MouseButton);
                _sink.MouseUp(binding.MouseButton);
            }
            else _mouseReferences[binding.MouseButton] = count - 1;
        }
    }

    public void MoveMouse(int x, int y)
    {
        if (x != 0 || y != 0) _sink.MouseMove(x, y);
    }

    public void ReleaseAll()
    {
        foreach (var button in _mouseReferences.Keys.ToArray()) _sink.MouseUp(button);
        foreach (var key in _keyReferences.Keys.Reverse().ToArray()) _sink.KeyUp(key);
        _mouseReferences.Clear();
        _keyReferences.Clear();
    }
}
