using System.IO;
using System.Runtime.InteropServices;

namespace GameControllerMapper;

[Flags]
internal enum GameInputGamepadButtons : uint
{
    Menu = 0x00000001,
    View = 0x00000002,
    A = 0x00000004,
    B = 0x00000008,
    X = 0x00000010,
    Y = 0x00000020,
    DPadUp = 0x00000040,
    DPadDown = 0x00000080,
    DPadLeft = 0x00000100,
    DPadRight = 0x00000200,
    LeftShoulder = 0x00000400,
    RightShoulder = 0x00000800,
    LeftThumbstick = 0x00001000,
    RightThumbstick = 0x00002000,
    PaddleLeft1 = 0x04000000,
    PaddleLeft2 = 0x08000000,
    PaddleRight1 = 0x10000000,
    PaddleRight2 = 0x20000000
}

internal readonly record struct GameInputDevice(nint Pointer, string Id, ushort VendorId, ushort ProductId);

internal readonly record struct GameInputFrame(
    uint Buttons,
    float LeftTrigger,
    float RightTrigger,
    float LeftX,
    float LeftY,
    float RightX,
    float RightY,
    int RawButtonCount,
    HashSet<int> RawButtons);

internal sealed class GameInputReader : IDisposable
{
    internal const uint EnableBackgroundInput = 0x00000040;
    private const uint GamepadInput = 0x00040000;
    private const uint ConnectedStatus = 0x00000001;
    private const int BlockingEnumeration = 2;
    private const int MaxControllerButtons = 1024;

    private nint _library;
    private IGameInput? _gameInput;

    public bool Available => _gameInput is not null;
    public string? InitializationError { get; private set; }

    public GameInputReader()
    {
        nint created = 0;
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "GameInput.dll");
            if (!File.Exists(path))
            {
                InitializationError = "缺少 Microsoft GameInput 运行时。请先安装程序目录中的 GameInputRedist.msi。";
                return;
            }

            _library = NativeLibrary.Load(path);
            var create = Marshal.GetDelegateForFunctionPointer<GameInputCreateDelegate>(
                NativeLibrary.GetExport(_library, "GameInputCreate"));
            var result = create(out created);
            if (result < 0 || created == 0)
            {
                InitializationError = $"Microsoft GameInput 初始化失败（0x{result:X8}）。请安装或修复 GameInputRedist.msi。";
                return;
            }

            _gameInput = (IGameInput)Marshal.GetObjectForIUnknown(created);
            _gameInput.SetFocusPolicy(EnableBackgroundInput);
        }
        catch (Exception ex)
        {
            InitializationError = $"Microsoft GameInput 初始化失败：{ex.Message}";
        }
        finally
        {
            if (created != 0) Marshal.Release(created);
        }
    }

    public IReadOnlyList<GameInputDevice> GetDevices()
    {
        if (_gameInput is null) return [];

        var pointers = new List<nint>();
        var context = GCHandle.Alloc(pointers);
        var callback = new DeviceCallbackDelegate(EnumerationCallback);
        ulong token = 0;
        try
        {
            var result = _gameInput.RegisterDeviceCallback(
                0,
                GamepadInput,
                ConnectedStatus,
                BlockingEnumeration,
                GCHandle.ToIntPtr(context),
                Marshal.GetFunctionPointerForDelegate(callback),
                out token);
            if (result < 0) return [];

            var devices = new List<GameInputDevice>(pointers.Count);
            foreach (var pointer in pointers)
            {
                try { devices.Add(ReadDeviceInfo(pointer)); }
                catch { Marshal.Release(pointer); }
            }
            pointers.Clear();
            return devices;
        }
        finally
        {
            if (token != 0)
            {
                _gameInput.StopCallback(token);
                _gameInput.UnregisterCallback(token);
            }
            foreach (var pointer in pointers) Marshal.Release(pointer);
            context.Free();
            GC.KeepAlive(callback);
        }
    }

    public bool TryRead(nint device, out GameInputFrame frame)
    {
        frame = default;
        if (_gameInput is null || device == 0) return false;

        IGameInputReading? reading = null;
        nint readingPointer = 0;
        nint buttonBuffer = 0;
        try
        {
            if (_gameInput.GetCurrentReading(GamepadInput, device, out readingPointer) < 0 || readingPointer == 0)
                return false;

            reading = (IGameInputReading)Marshal.GetObjectForIUnknown(readingPointer);
            if (!reading.GetGamepadState(out var state)) return false;

            var rawButtons = new HashSet<int>();
            var rawButtonCount = reading.GetControllerButtonCount();
            if (rawButtonCount <= MaxControllerButtons && rawButtonCount > 0)
            {
                buttonBuffer = Marshal.AllocHGlobal((int)rawButtonCount);
                var written = Math.Min(rawButtonCount, reading.GetControllerButtonState(rawButtonCount, buttonBuffer));
                for (var i = 0; i < written; i++)
                    if (Marshal.ReadByte(buttonBuffer, i) != 0) rawButtons.Add(i);
            }
            else rawButtonCount = 0;

            frame = new GameInputFrame(
                state.Buttons,
                state.LeftTrigger,
                state.RightTrigger,
                state.LeftThumbstickX,
                state.LeftThumbstickY,
                state.RightThumbstickX,
                state.RightThumbstickY,
                (int)rawButtonCount,
                rawButtons);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (buttonBuffer != 0) Marshal.FreeHGlobal(buttonBuffer);
            if (reading is not null && Marshal.IsComObject(reading)) Marshal.ReleaseComObject(reading);
            if (readingPointer != 0) Marshal.Release(readingPointer);
        }
    }

    public static void ReleaseDevice(nint device)
    {
        if (device != 0) Marshal.Release(device);
    }

    public void Dispose()
    {
        if (_gameInput is not null && Marshal.IsComObject(_gameInput)) Marshal.FinalReleaseComObject(_gameInput);
        _gameInput = null;
        if (_library != 0) NativeLibrary.Free(_library);
        _library = 0;
    }

    private static GameInputDevice ReadDeviceInfo(nint pointer)
    {
        IGameInputDevice? device = null;
        try
        {
            device = (IGameInputDevice)Marshal.GetObjectForIUnknown(pointer);
            if (device.GetDeviceInfo(out var info) < 0 || info == 0)
                throw new COMException("无法读取 GameInput 设备信息。");

            var id = new byte[32];
            Marshal.Copy(info + 26, id, 0, id.Length);
            return new GameInputDevice(
                pointer,
                Convert.ToHexString(id),
                (ushort)Marshal.ReadInt16(info, 0),
                (ushort)Marshal.ReadInt16(info, 2));
        }
        finally
        {
            if (device is not null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
        }
    }

    private static void EnumerationCallback(
        ulong callbackToken,
        nint context,
        nint device,
        ulong timestamp,
        uint currentStatus,
        uint previousStatus)
    {
        if (context == 0 || device == 0 || (currentStatus & ConnectedStatus) == 0) return;
        if (GCHandle.FromIntPtr(context).Target is List<nint> devices)
        {
            Marshal.AddRef(device);
            devices.Add(device);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GameInputCreateDelegate(out nint gameInput);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DeviceCallbackDelegate(
        ulong callbackToken,
        nint context,
        nint device,
        ulong timestamp,
        uint currentStatus,
        uint previousStatus);

    [ComImport]
    [Guid("20EFC1C7-5D9A-43BA-B26F-B807FA48609C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGameInput
    {
        [PreserveSig] ulong GetCurrentTimestamp();
        [PreserveSig] int GetCurrentReading(uint inputKind, nint device, out nint reading);
        [PreserveSig] int GetNextReading(nint referenceReading, uint inputKind, nint device, out nint reading);
        [PreserveSig] int GetPreviousReading(nint referenceReading, uint inputKind, nint device, out nint reading);
        [PreserveSig] int RegisterReadingCallback(nint device, uint inputKind, nint context, nint callback, out ulong token);
        [PreserveSig] int RegisterDeviceCallback(nint device, uint inputKind, uint statusFilter, int enumerationKind, nint context, nint callback, out ulong token);
        [PreserveSig] int RegisterSystemButtonCallback(nint device, uint buttonFilter, nint context, nint callback, out ulong token);
        [PreserveSig] int RegisterKeyboardLayoutCallback(nint device, nint context, nint callback, out ulong token);
        void StopCallback(ulong token);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool UnregisterCallback(ulong token);
        [PreserveSig] int CreateDispatcher(out nint dispatcher);
        [PreserveSig] int FindDeviceFromId(nint value, out nint device);
        [PreserveSig] int FindDeviceFromPlatformString([MarshalAs(UnmanagedType.LPWStr)] string value, out nint device);
        void SetFocusPolicy(uint policy);
        [PreserveSig] int CreateAggregateDevice(uint inputKind, nint deviceId);
        [PreserveSig] int DisableAggregateDevice(nint deviceId);
    }

    [ComImport]
    [Guid("63E2F38B-A399-4275-8AE7-D4C6E524D12A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGameInputDevice
    {
        [PreserveSig] int GetDeviceInfo(out nint info);
    }

    [ComImport]
    [Guid("C81C4CDE-ED1A-4631-A30F-C556A6241A1F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGameInputReading
    {
        [PreserveSig] uint GetInputKind();
        [PreserveSig] ulong GetTimestamp();
        void GetDevice(out nint device);
        [PreserveSig] uint GetControllerAxisCount();
        [PreserveSig] uint GetControllerAxisState(uint count, nint states);
        [PreserveSig] uint GetControllerButtonCount();
        [PreserveSig] uint GetControllerButtonState(uint count, nint states);
        [PreserveSig] uint GetControllerSwitchCount();
        [PreserveSig] uint GetControllerSwitchState(uint count, nint states);
        [PreserveSig] uint GetKeyCount();
        [PreserveSig] uint GetKeyState(uint count, nint states);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetMouseState(nint state);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetSensorsState(nint state);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetArcadeStickState(nint state);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetFlightStickState(nint state);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetGamepadState(out NativeGamepadState state);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetRacingWheelState(nint state);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool GetRawReport(out nint report);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGamepadState
    {
        public uint Buttons;
        public float LeftTrigger;
        public float RightTrigger;
        public float LeftThumbstickX;
        public float LeftThumbstickY;
        public float RightThumbstickX;
        public float RightThumbstickY;
    }
}
