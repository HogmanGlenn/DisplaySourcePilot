namespace DisplaySourcePilot.Core.Control;

public interface ISamsungRemoteControl
{
    Task SendKeyAsync(string key, CancellationToken cancellationToken = default);
}
