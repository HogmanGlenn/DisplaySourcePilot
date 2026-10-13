using DisplaySourcePilot.Core.Configuration;

namespace DisplaySourcePilot.Core.Control;

public interface ISamsungRemoteControl : IAsyncDisposable
{
    IReadOnlyList<TimeSpan> ConnectionTimes { get; }

    Task ConnectAsync(
        ConfiguredDisplay display,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task SendKeyAsync(string key, CancellationToken cancellationToken = default);
}
