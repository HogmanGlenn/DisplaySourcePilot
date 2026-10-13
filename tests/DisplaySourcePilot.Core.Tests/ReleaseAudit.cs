using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Security;

internal static class ReleaseAudit
{
    private static readonly string[] RequiredFiles =
    [
        "DisplaySourcePilot.exe", "DisplaySourcePilot.dll", "DisplaySourcePilot.Core.dll",
        "DisplaySourcePilot.deps.json", "DisplaySourcePilot.runtimeconfig.json",
        "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "Microsoft.ui.xaml.dll",
        "App.xbf", "MainPage.xbf", "MainWindow.xbf", "DisplaySourcePilot.pri", "Assets/AppIcon.ico"
    ];

    public static int Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        foreach (var file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(directory, file)))
            {
                throw new InvalidOperationException($"Release is missing {file}.");
            }
        }

        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "DisplaySourcePilot.runtimeconfig.json")));
        var options = runtime.RootElement.GetProperty("runtimeOptions");
        if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _))
        {
            throw new InvalidOperationException("The release requires a separately installed .NET runtime.");
        }

        using var executable = new BinaryReader(File.OpenRead(Path.Combine(directory, "DisplaySourcePilot.exe")));
        executable.BaseStream.Position = 0x3c;
        executable.BaseStream.Position = executable.ReadInt32() + 4;
        var expectedMachine = Path.GetFileName(directory) switch
        {
            "win-x64" => 0x8664,
            "win-arm64" => 0xaa64,
            _ => throw new InvalidOperationException("Unknown release architecture.")
        };
        if (executable.ReadUInt16() != expectedMachine)
        {
            throw new InvalidOperationException("The executable does not match the release architecture.");
        }

        var values = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            values.Add(profile);
            values.Add(profile.Replace('\\', '/'));
        }

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplaySourcePilot", "settings.json");
        if (File.Exists(settingsPath))
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath),
                new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            if (settings?.Display is { } display)
            {
                values.AddRange(new[] { display.Address, display.HardwareId, display.Id }
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!));
                var store = new CredentialStore();
                foreach (var identity in new[] { display.HardwareId, display.Address }.Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    foreach (var prefix in new[] { "DisplaySourcePilot:Samsung:", "DisplaySwitch:Samsung:" })
                    {
                        var token = store.Read(prefix + identity);
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            values.Add(token);
                        }
                    }
                }
            }
        }

        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!IsAllowedFileName(name))
            {
                throw new InvalidOperationException($"Private or unexpected file in release: {name}.");
            }

            if (ContainsPrivateValue(File.ReadAllBytes(file), values))
            {
                throw new InvalidOperationException($"Release privacy check failed in {Path.GetRelativePath(directory, file)}. The matched value is withheld.");
            }
        }

        Console.WriteLine($"PASS  Release architecture, bundled runtime, and privacy checks ({Path.GetFileName(directory)}, {files.Length} files).");
        return 0;
    }

    internal static bool IsAllowedFileName(string name)
    {
        var extension = Path.GetExtension(name);
        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return name is "DisplaySourcePilot.deps.json" or "DisplaySourcePilot.runtimeconfig.json";
        }

        return new[] { ".dll", ".exe", ".html", ".ico", ".mui", ".png", ".pri", ".winmd", ".xbf" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool ContainsPrivateValue(byte[] data, IEnumerable<string> values)
    {
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
        {
            foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
            {
                if (data.AsSpan().IndexOf(encoding.GetBytes(value)) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
