using System.Text.Json;

namespace DisplaySourcePilot.Core.Configuration;

public sealed class WindowPlacementStore
{
    public WindowPlacementStore(string? rootDirectory = null)
    {
        var root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplaySourcePilot");
        PathName = Path.Combine(root, "window.json");
    }

    public string PathName { get; }

    public WindowPlacement? Load()
    {
        if (!File.Exists(PathName))
        {
            return null;
        }

        try
        {
            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(PathName));
            return placement is { IsValid: true } ? placement : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(WindowPlacement placement)
    {
        if (!placement.IsValid)
        {
            throw new ArgumentException("The window placement is invalid.", nameof(placement));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var temporaryPath = PathName + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(placement));
        File.Move(temporaryPath, PathName, true);
    }
}
