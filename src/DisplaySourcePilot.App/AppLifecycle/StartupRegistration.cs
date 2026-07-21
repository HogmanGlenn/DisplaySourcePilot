using Microsoft.Win32;

namespace DisplaySourcePilot.App.AppLifecycle;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DisplaySourcePilot";
    private const string LegacyValueName = "Display Switch";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Windows startup settings are unavailable.");
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (enabled)
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The application path is unavailable.");
            key.SetValue(ValueName, $"\"{processPath}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
