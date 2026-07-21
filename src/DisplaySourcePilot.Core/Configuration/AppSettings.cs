namespace DisplaySourcePilot.Core.Configuration;

public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public bool RunAtStartup { get; set; } = true;

    public bool KeepRunning { get; set; } = true;

    public HotkeySettings Hotkeys { get; set; } = new();

    public ConfiguredDisplay? Display { get; set; }
}
