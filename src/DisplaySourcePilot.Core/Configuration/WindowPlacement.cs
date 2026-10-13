using System.Text.Json.Serialization;

namespace DisplaySourcePilot.Core.Configuration;

public sealed record WindowPlacement(
    int X,
    int Y,
    int Width,
    int Height,
    uint Dpi = 96,
    bool Maximized = false,
    bool UserResized = false)
{
    [JsonIgnore]
    public bool IsValid => Width is >= 100 and <= 100_000 && Height is >= 100 and <= 100_000 && Dpi is >= 48 and <= 768;

    public WindowPlacement Restore(int workX, int workY, int workWidth, int workHeight, uint dpi, int minimumWidth, int minimumHeight)
    {
        var scale = dpi / (double)Dpi;
        var width = Math.Clamp((int)Math.Round(Width * scale), Math.Min(minimumWidth, workWidth), workWidth);
        var height = Math.Clamp((int)Math.Round(Height * scale), Math.Min(minimumHeight, workHeight), workHeight);
        var x = (int)Math.Clamp((long)X, workX, (long)workX + workWidth - width);
        var y = (int)Math.Clamp((long)Y, workY, (long)workY + workHeight - height);
        return this with { X = x, Y = y, Width = width, Height = height, Dpi = dpi };
    }
}
