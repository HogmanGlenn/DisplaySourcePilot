using System.Text.Json.Serialization;

namespace DisplaySourcePilot.Core.Configuration;

public sealed class HotkeySettings
{
    public HotkeyBinding Hdmi { get; set; } = new(HotkeyModifiers.ControlAlt, "H");

    public HotkeyBinding DisplayPort { get; set; } = new(HotkeyModifiers.ControlAlt, "P");

    [JsonIgnore]
    public bool IsValid =>
        Hdmi.IsValid &&
        DisplayPort.IsValid &&
        (Hdmi.Modifiers != DisplayPort.Modifiers ||
         !string.Equals(Hdmi.Key, DisplayPort.Key, StringComparison.OrdinalIgnoreCase));
}
