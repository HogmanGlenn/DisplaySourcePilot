using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Control;

public static class SamsungSourceProfiles
{
    private static readonly SamsungSourceProfile[] Profiles =
    [
        new(
            ["LS32DG80"],
            new InputSource("hdmi2", "HDMI 2", InputKind.Hdmi, Route: RemoteRoute.Up()),
            new InputSource("displayport", "DisplayPort", InputKind.DisplayPort, Route: RemoteRoute.Down()),
            RenderBudgetMilliseconds: 650,
            MinimumDelayMilliseconds: 700)
    ];

    public static SamsungSourceProfile? Find(string model) =>
        Profiles.FirstOrDefault(profile => profile.Matches(model));
}
