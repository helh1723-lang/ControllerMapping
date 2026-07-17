using System.Text.Json.Serialization;

namespace GameControllerMapper;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputKind
{
    Keyboard,
    MouseButton,
    MouseWheel
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseButtonOutput
{
    Left,
    Right,
    Middle,
    X1,
    X2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StickMode
{
    Directions,
    Mouse,
    Disabled
}

public sealed class OutputBinding
{
    public OutputKind Kind { get; set; }
    public List<ushort> Keys { get; set; } = [];
    public MouseButtonOutput MouseButton { get; set; }
    public int WheelDelta { get; set; } = 120;

    public OutputBinding Clone() => new()
    {
        Kind = Kind,
        Keys = [.. Keys],
        MouseButton = MouseButton,
        WheelDelta = WheelDelta
    };
}

public sealed class MappingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "默认配置";
    public Dictionary<string, OutputBinding> Bindings { get; set; } = new(StringComparer.Ordinal);
    public StickMode LeftStickMode { get; set; } = StickMode.Directions;
    public StickMode RightStickMode { get; set; } = StickMode.Mouse;
    public double StickDeadzone { get; set; } = 0.18;
    public double DirectionThreshold { get; set; } = 0.55;
    public double TriggerThreshold { get; set; } = 0.50;
    public double MouseSpeed { get; set; } = 900;

    public MappingProfile Clone(string? newName = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = newName ?? Name,
        Bindings = Bindings.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal),
        LeftStickMode = LeftStickMode,
        RightStickMode = RightStickMode,
        StickDeadzone = StickDeadzone,
        DirectionThreshold = DirectionThreshold,
        TriggerThreshold = TriggerThreshold,
        MouseSpeed = MouseSpeed
    };

    public MappingProfile RuntimeCopy() => new()
    {
        Id = Id,
        Name = Name,
        Bindings = Bindings.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal),
        LeftStickMode = LeftStickMode,
        RightStickMode = RightStickMode,
        StickDeadzone = StickDeadzone,
        DirectionThreshold = DirectionThreshold,
        TriggerThreshold = TriggerThreshold,
        MouseSpeed = MouseSpeed
    };
}

public sealed class ProfileDocument
{
    public Guid SelectedProfileId { get; set; }
    public List<MappingProfile> Profiles { get; set; } = [];
}

public sealed record SourceControl(string Id, string DisplayName, bool IsRaw = false);

public sealed record DeviceFingerprint(string DisplayName, ushort VendorId, ushort ProductId, string InterfaceId)
{
    [JsonIgnore]
    public string Key => $"{VendorId:X4}:{ProductId:X4}:{InterfaceId}";
}

public sealed class InputSnapshot
{
    public static readonly InputSnapshot Empty = new();

    public HashSet<string> Buttons { get; init; } = new(StringComparer.Ordinal);
    public HashSet<int> RawButtons { get; init; } = [];
    public string DeviceKey { get; init; } = "";
    public double LeftTrigger { get; init; }
    public double RightTrigger { get; init; }
    public double LeftX { get; init; }
    public double LeftY { get; init; }
    public double RightX { get; init; }
    public double RightY { get; init; }
}

public static class SourceCatalog
{
    public static readonly IReadOnlyList<SourceControl> Standard =
    [
        new("A", "A 键"), new("B", "B 键"), new("X", "X 键"), new("Y", "Y 键"),
        new("LB", "LB 键"), new("RB", "RB 键"), new("LT", "LT 扳机"), new("RT", "RT 扳机"),
        new("LS", "左摇杆按下"), new("RS", "右摇杆按下"),
        new("DPad.Up", "十字键 上"), new("DPad.Down", "十字键 下"),
        new("DPad.Left", "十字键 左"), new("DPad.Right", "十字键 右"),
        new("View", "视图键"), new("Menu", "菜单键"),
        new("Paddle1", "背键 1"), new("Paddle2", "背键 2"),
        new("Paddle3", "背键 3"), new("Paddle4", "背键 4"),
        new("LS.Up", "左摇杆 上"), new("LS.Down", "左摇杆 下"),
        new("LS.Left", "左摇杆 左"), new("LS.Right", "左摇杆 右"),
        new("RS.Up", "右摇杆 上"), new("RS.Down", "右摇杆 下"),
        new("RS.Left", "右摇杆 左"), new("RS.Right", "右摇杆 右")
    ];

    public static string RawId(string deviceKey, int index) => $"Raw|{deviceKey}|{index}";

    public static bool TryParseRaw(string id, out string deviceKey, out int index)
    {
        deviceKey = "";
        index = -1;
        var parts = id.Split('|');
        if (parts.Length != 3 || parts[0] != "Raw" || !int.TryParse(parts[2], out index)) return false;
        deviceKey = parts[1];
        return true;
    }
}
