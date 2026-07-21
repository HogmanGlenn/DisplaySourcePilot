using System.Text.Json.Serialization;

namespace DisplaySourcePilot.Core.Models;

public sealed record RemoteRoute(IReadOnlyList<string> Keys)
{
    [JsonIgnore]
    public bool IsSafeNavigation =>
        Keys is { Count: >= 1 and <= 8 } &&
        Keys.All(key => key is "KEY_UP" or "KEY_DOWN" or "KEY_LEFT" or "KEY_RIGHT") &&
        !Keys.Zip(Keys.Skip(1)).Any(pair => Invert(pair.First) == pair.Second);

    public static RemoteRoute Up(int steps = 1) => Repeat("KEY_UP", steps);

    public static RemoteRoute Down(int steps = 1) => Repeat("KEY_DOWN", steps);

    public static RemoteRoute Left(int steps = 1) => Repeat("KEY_LEFT", steps);

    public static RemoteRoute Right(int steps = 1) => Repeat("KEY_RIGHT", steps);

    public RemoteRoute Reverse() => new(Keys.Reverse().Select(Invert).ToArray());

    private static RemoteRoute Repeat(string key, int steps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);

        return new RemoteRoute(Enumerable.Repeat(key, steps).ToArray());
    }

    private static string Invert(string key) => key switch
    {
        "KEY_UP" => "KEY_DOWN",
        "KEY_DOWN" => "KEY_UP",
        "KEY_LEFT" => "KEY_RIGHT",
        "KEY_RIGHT" => "KEY_LEFT",
        _ => throw new InvalidOperationException($"Cannot reverse remote key {key}.")
    };
}
