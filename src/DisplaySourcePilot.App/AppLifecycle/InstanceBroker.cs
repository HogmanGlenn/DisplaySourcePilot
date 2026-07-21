using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace DisplaySourcePilot.App.AppLifecycle;

internal sealed class InstanceBroker : IAsyncDisposable
{
    private const string MutexName = "Local\\DisplaySourcePilot.Product.Instance";
    private static readonly string PipeName = $"DisplaySourcePilot.Product.Commands.{Process.GetCurrentProcess().SessionId}";
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentQueue<string> _pendingCommands = new();
    private Mutex? _mutex;
    private Task? _listener;

    public event Action<string>? CommandReceived;

    public bool TryBecomePrimary()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _listener = ListenAsync(_cancellation.Token);
        return true;
    }

    public static async Task SendAsync(string command, CancellationToken cancellationToken = default)
    {
        using var client = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(timeout.Token);
        await using var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
    }

    public void FlushPendingCommands()
    {
        while (_pendingCommands.TryDequeue(out var command))
        {
            CommandReceived?.Invoke(command);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_listener is not null)
        {
            try
            {
                await _listener;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    var handler = CommandReceived;
                    if (handler is null)
                    {
                        _pendingCommands.Enqueue(command);
                    }
                    else
                    {
                        handler(command);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A disconnected command client must not stop future commands.
            }
        }
    }
}
