using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DisplaySourcePilot.Core.Configuration;

public sealed class SettingsStore
{
    private const string ProductDirectoryName = "DisplaySourcePilot";
    private const string LegacyProductDirectoryName = "Display Switch";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(string? rootDirectory = null)
    {
        if (rootDirectory is not null)
        {
            RootDirectory = rootDirectory;
            return;
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        RootDirectory = Path.Combine(localApplicationData, ProductDirectoryName);
        TryMigrateLegacySettings(Path.Combine(localApplicationData, LegacyProductDirectoryName));
    }

    public string RootDirectory { get; }

    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            AppSettings? settings;
            try
            {
                await using var stream = File.OpenRead(SettingsPath);
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                PreserveInvalidSettings();
                return new AppSettings();
            }
            if (settings is not { Version: AppSettings.CurrentVersion })
            {
                return new AppSettings();
            }

            if (settings.Hotkeys is not { IsValid: true })
            {
                settings.Hotkeys = new HotkeySettings();
            }

            if (settings.Display is { IsValid: false })
            {
                settings.Display = null;
            }
            else if (settings.Display is { Method: Models.ControlMethod.SamsungTizen } display)
            {
                display.MenuDelayMilliseconds = Math.Clamp(display.MenuDelayMilliseconds, 700, 1200);
            }

            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var temporaryPath = SettingsPath + ".tmp";

            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, SettingsPath, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PreserveInvalidSettings()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(RootDirectory, $"settings.invalid-{timestamp}.json");
        try
        {
            File.Move(SettingsPath, backupPath, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keep the original file when Windows cannot move it safely.
        }
    }

    private void TryMigrateLegacySettings(string legacyRootDirectory)
    {
        var legacySettingsPath = Path.Combine(legacyRootDirectory, "settings.json");
        if (File.Exists(SettingsPath) || !File.Exists(legacySettingsPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(RootDirectory);
            File.Copy(legacySettingsPath, SettingsPath, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed migration leaves the existing installation untouched.
        }
    }
}
