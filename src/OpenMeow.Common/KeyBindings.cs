using System.Globalization;
using System.Text;

namespace OpenMeow;

public readonly record struct KeyBindingDefinition(string Id, string Label, string Group, int DefaultKey);

public sealed class KeyBindings
{
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenMeow", "keybindings.cfg");

    public static IReadOnlyList<KeyBindingDefinition> Definitions { get; } = new KeyBindingDefinition[]
    {
        new("MoveForward", "前進", "移動・視点", 0x57),
        new("MoveBack", "後退", "移動・視点", 0x53),
        new("MoveLeft", "左移動", "移動・視点", 0x41),
        new("MoveRight", "右移動", "移動・視点", 0x44),
        new("MoveUp", "上昇", "移動・視点", 0x45),
        new("MoveDown", "下降", "移動・視点", 0x51),
        new("TurnLeft", "左旋回", "移動・視点", 0x25),
        new("TurnRight", "右旋回", "移動・視点", 0x27),
        new("LookUp", "上を見る", "移動・視点", 0x26),
        new("LookDown", "下を見る", "移動・視点", 0x28),
        new("Fast", "高速移動", "移動・視点", 0xA0),
        new("Slow", "低速移動", "移動・視点", 0xA2),
        new("Reset", "位置をリセット", "移動・視点", 0x08),
        new("RightPosition", "右手の位置", "手・スティック", 0x20),
        new("RightWrist", "右手の手首", "手・スティック", 0x04),
        new("LeftPosition", "左手の位置", "手・スティック", 0x05),
        new("LeftPositionAlt", "左手位置の代替", "手・スティック", 0xA4),
        new("LeftWrist", "左手の手首", "手・スティック", 0x06),
        new("LeftWristMouse", "左手首のマウス補助", "手・スティック", 0x04),
        new("LeftStick", "左パッド", "手・スティック", 0x09),
        new("RightStick", "右パッド", "手・スティック", 0x52),
        new("DepthForward", "手を近づける", "手・スティック", 0x21),
        new("DepthBack", "手を遠ざける", "手・スティック", 0x22),
        new("LeftTrigger", "左トリガー", "左手ボタン", 0x5A),
        new("LeftGrip", "左グリップ", "左手ボタン", 0x58),
        new("LeftMenu", "左メニュー", "左手ボタン", 0x43),
        new("LeftPadClick", "左パッドクリック", "左手ボタン", 0x56),
        new("LeftPadUp", "左パッド上", "左手ボタン", 0x54),
        new("LeftPadLeft", "左パッド左", "左手ボタン", 0x46),
        new("LeftPadDown", "左パッド下", "左手ボタン", 0x47),
        new("LeftPadRight", "左パッド右", "左手ボタン", 0x48),
        new("LeftSystem", "左システム", "左手ボタン", 0x76),
        new("RightTrigger", "右トリガー", "右手ボタン", 0x55),
        new("RightGrip", "右グリップ", "右手ボタン", 0x4F),
        new("RightMenu", "右メニュー", "右手ボタン", 0x50),
        new("RightPadClick", "右パッドクリック", "右手ボタン", 0x4D),
        new("RightPadUp", "右パッド上", "右手ボタン", 0x49),
        new("RightPadLeft", "右パッド左", "右手ボタン", 0x4A),
        new("RightPadDown", "右パッド下", "右手ボタン", 0x4B),
        new("RightPadRight", "右パッド右", "右手ボタン", 0x4C),
        new("RightSystem", "右システム", "右手ボタン", 0x77),
        new("GripHoldLeft", "左グリップ保持", "補助操作", 0x42),
        new("GripHoldRight", "右グリップ保持", "補助操作", 0x59),
        new("InvertX", "左右反転", "補助操作", 0x74),
        new("InvertY", "上下反転", "補助操作", 0x75),
        new("CaptureToggle", "入力のON/OFF", "補助操作", 0x78),
        new("TargetNext", "フォールバック対象切替", "補助操作", 0x79),
    };

    private readonly Dictionary<string, int> _values = new(StringComparer.Ordinal);

    private KeyBindings() { }

    public static KeyBindings Defaults()
    {
        var result = new KeyBindings();
        foreach (var definition in Definitions) result._values[definition.Id] = definition.DefaultKey;
        return result;
    }

    public KeyBindings Clone()
    {
        var result = new KeyBindings();
        foreach (var definition in Definitions) result._values[definition.Id] = Get(definition.Id);
        return result;
    }

    public int Get(string id)
    {
        if (_values.TryGetValue(id, out int value) && value is >= 0 and <= 255) return value;
        return Definitions.FirstOrDefault(x => x.Id == id).DefaultKey;
    }

    public void Set(string id, int key)
    {
        if (Definitions.Any(x => x.Id == id) && key is >= 0 and <= 255) _values[id] = key;
    }

    public static KeyBindings LoadOrDefault()
    {
        var result = Defaults();
        try
        {
            if (!File.Exists(ConfigPath)) return result;
            foreach (string line in File.ReadAllLines(ConfigPath))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                string id = line[..separator].Trim();
                if (int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int key)) result.Set(id, key);
            }
        }
        catch { }
        return result;
    }

    public void Save()
    {
        string directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);
        string temp = ConfigPath + ".tmp";
        File.WriteAllLines(temp,
            Definitions.Select(x => $"{x.Id}={Get(x.Id)}"), Encoding.UTF8);
        File.Move(temp, ConfigPath, overwrite: true);
    }
}
