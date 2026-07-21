namespace DisplaySourcePilot.Core.Control;

public sealed class CurrentInputUnknownException : InvalidOperationException
{
    public CurrentInputUnknownException()
        : base("Confirm the active input before switching. No command was sent to the monitor.")
    {
    }
}
