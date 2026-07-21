using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Control;

public sealed class SamsungController
{
    private const int MinimumMenuDelayMilliseconds = 700;
    private const int NavigationDelayMilliseconds = 300;
    private const int SelectionDelayMilliseconds = 900;
    private readonly ISamsungRemoteControl _remote;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SamsungController(
        ISamsungRemoteControl remote,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _remote = remote;
        _delay = delay ?? Task.Delay;
    }

    public async Task SwitchAsync(
        ConfiguredDisplay display,
        InputSource input,
        CancellationToken cancellationToken = default)
    {
        if (input.Route is not { } route)
        {
            throw new InvalidOperationException($"{input.Label} has not been calibrated for this monitor.");
        }

        if (!route.IsSafeNavigation)
        {
            throw new InvalidOperationException($"{input.Label} has an unsafe source-menu route. Set up the monitor again.");
        }

        await _remote.SendKeyAsync("KEY_SOURCE", cancellationToken);
        await _delay(
            TimeSpan.FromMilliseconds(Math.Clamp(display.MenuDelayMilliseconds, MinimumMenuDelayMilliseconds, 1200)),
            cancellationToken);
        foreach (var key in route.Keys)
        {
            await _remote.SendKeyAsync(key, cancellationToken);
            await _delay(TimeSpan.FromMilliseconds(NavigationDelayMilliseconds), cancellationToken);
        }

        await _remote.SendKeyAsync("KEY_ENTER", cancellationToken);
        await _delay(TimeSpan.FromMilliseconds(SelectionDelayMilliseconds), cancellationToken);
    }
}
