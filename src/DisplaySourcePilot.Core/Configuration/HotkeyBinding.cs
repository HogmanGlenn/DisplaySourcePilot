using System.Text.Json.Serialization;

namespace DisplaySourcePilot.Core.Configuration;

public enum HotkeyModifiers
{
    ControlAlt,
    ControlShift,
    AltShift,
    ControlAltShift
}

public sealed record HotkeyBinding(HotkeyModifiers Modifiers, string Key)
{
    [JsonIgnore]
    public string DisplayText => $"{ModifierText}+{Key.ToUpperInvariant()}";

    [JsonIgnore]
    public string ModifierText => Modifiers switch
    {
        HotkeyModifiers.ControlAlt => "Ctrl+Alt",
        HotkeyModifiers.ControlShift => "Ctrl+Shift",
        HotkeyModifiers.AltShift => "Alt+Shift",
        HotkeyModifiers.ControlAltShift => "Ctrl+Alt+Shift",
        _ => throw new ArgumentOutOfRangeException(nameof(Modifiers))
    };

    [JsonIgnore]
    public bool IsValid =>
        Enum.IsDefined(Modifiers) &&
        Key is { Length: 1 } &&
        char.ToUpperInvariant(Key[0]) is >= 'A' and <= 'Z';
}
