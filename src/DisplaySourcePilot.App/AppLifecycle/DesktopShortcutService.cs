using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.App.AppLifecycle;

internal static class DesktopShortcutService
{
    private static readonly object RegistryLock = new();

    public static string Create(ConfiguredDisplay display, InputKind input)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application path is unavailable.");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
        {
            throw new InvalidOperationException("The Windows desktop folder is unavailable.");
        }

        var displayName = WebUtility.HtmlDecode(display.Name);
        var sourceName = input == InputKind.Hdmi ? display.Hdmi.Label : display.DisplayPort.Label;
        var fileName = SanitizeFileName($"{displayName} - {sourceName}");
        var shortcutPath = FindAvailablePath(desktop, fileName, executablePath);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows shortcut support could not be started.");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath]);
            if (shortcut is null)
            {
                throw new InvalidOperationException("Windows could not create the desktop shortcut.");
            }

            var shortcutType = shortcut.GetType();
            SetProperty(shortcutType, shortcut, "TargetPath", executablePath);
            SetProperty(
                shortcutType,
                shortcut,
                "Arguments",
                $"--display {display.ShortcutToken} --switch {(input == InputKind.Hdmi ? "hdmi" : "displayport")}");
            SetProperty(shortcutType, shortcut, "WorkingDirectory", Path.GetDirectoryName(executablePath) ?? string.Empty);
            SetProperty(shortcutType, shortcut, "IconLocation", ShortcutIcon(executablePath));
            SetProperty(shortcutType, shortcut, "Description", $"Switch {displayName} to {sourceName}");
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            Track(shortcutPath);
            return shortcutPath;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    public static void RemoveTracked()
    {
        var executablePath = Environment.ProcessPath;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(desktop))
        {
            return;
        }

        lock (RegistryLock)
        {
            var registryPath = ShortcutRegistryPath();
            foreach (var shortcutPath in ReadRegistry(registryPath))
            {
                if (!IsDesktopShortcut(shortcutPath, desktop) ||
                    !string.Equals(ReadTarget(shortcutPath), executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Delete(shortcutPath);
            }

            File.Delete(registryPath);
        }
    }

    private static void SetProperty(Type type, object instance, string name, string value) =>
        type.InvokeMember(name, BindingFlags.SetProperty, null, instance, [value]);

    private static string FindAvailablePath(string desktop, string fileName, string executablePath)
    {
        for (var suffix = 1; suffix < 1000; suffix++)
        {
            var name = suffix == 1 ? fileName : $"{fileName} ({suffix})";
            var path = Path.Combine(desktop, $"{name}.lnk");
            if (!File.Exists(path) || string.Equals(ReadTarget(path), executablePath, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        throw new InvalidOperationException("A free desktop shortcut name could not be found.");
    }

    private static string ShortcutIcon(string executablePath)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        return File.Exists(iconPath) ? $"{iconPath},0" : $"{executablePath},0";
    }

    private static void Track(string shortcutPath)
    {
        lock (RegistryLock)
        {
            var registryPath = ShortcutRegistryPath();
            var paths = new HashSet<string>(ReadRegistry(registryPath), StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(shortcutPath)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
            var temporaryPath = registryPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(paths.Order(StringComparer.OrdinalIgnoreCase)));
            File.Move(temporaryPath, registryPath, true);
        }
    }

    private static IReadOnlyList<string> ReadRegistry(string registryPath)
    {
        if (!File.Exists(registryPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(registryPath)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadTarget(string shortcutPath)
    {
        if (!File.Exists(shortcutPath))
        {
            return null;
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return null;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath]);
            return shortcut?.GetType().InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;
        }
        catch (Exception exception) when (
            exception is COMException or TargetInvocationException or MissingMethodException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static bool IsDesktopShortcut(string shortcutPath, string desktop)
    {
        try
        {
            var fullPath = Path.GetFullPath(shortcutPath);
            var desktopRoot = Path.GetFullPath(desktop) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(desktopRoot, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string ShortcutRegistryPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DisplaySourcePilot",
        "shortcuts.json");

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return sanitized.Length <= 100 ? sanitized : sanitized[..100].TrimEnd();
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
