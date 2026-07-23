using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Discovery;
using DisplaySourcePilot.Core.Security;

namespace DisplaySourcePilot.Core.Control;

public sealed class SamsungRemoteClient : ISamsungRemoteControl, IAsyncDisposable
{
    private const string RemoteName = "DisplaySourcePilot";
    private const string CredentialPrefix = "DisplaySourcePilot:Samsung:";
    private const string LegacyCredentialPrefix = "DisplaySwitch:Samsung:";
    private readonly CredentialStore _credentialStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<TimeSpan> _connectionTimes = [];
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private ConfiguredDisplay? _display;
    private string? _token;

    public SamsungRemoteClient(CredentialStore? credentialStore = null)
    {
        _credentialStore = credentialStore ?? new CredentialStore();
    }

    private bool IsConnected => _socket?.State == WebSocketState.Open;

    public IReadOnlyList<TimeSpan> ConnectionTimes => _connectionTimes;

    public async Task ConnectAsync(
        ConfiguredDisplay display,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected && SameDisplay(display, _display))
            {
                return;
            }

            await CloseSocketAsync();
            ValidateEndpoint(display);
            if (_display is not null && !SameDisplay(display, _display))
            {
                _connectionTimes.Clear();
            }

            _display = display;
            _token = ReadCredential(display);
            var certificatePin = display.Port == 8002
                ? SamsungCertificatePins.Read(_credentialStore, display.HardwareId, display.Address!)
                : null;

            if (string.IsNullOrWhiteSpace(_token) && display.Port == 8002)
            {
                progress?.Report("Approve DisplaySourcePilot on the monitor.");
            }
            else
            {
                progress?.Report("Connecting to the monitor...");
            }

            try
            {
                await OpenSocketAsync(display, _token, certificatePin, cancellationToken);
            }
            catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(_token))
            {
                DeleteCredentials(display);
                _token = null;
                progress?.Report("Approve DisplaySourcePilot on the monitor.");
                await OpenSocketAsync(display, null, certificatePin, cancellationToken);
            }

            progress?.Report("Monitor connected.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_display is null)
            {
                throw new InvalidOperationException("The Samsung display has not been connected.");
            }

            if (!IsConnected)
            {
                await CloseSocketAsync();
                var certificatePin = _display.Port == 8002
                    ? SamsungCertificatePins.Read(_credentialStore, _display.HardwareId, _display.Address!)
                    : null;
                await OpenSocketAsync(_display, _token, certificatePin, cancellationToken);
            }

            await SendKeyCoreAsync(key, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await CloseSocketAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task OpenSocketAsync(
        ConfiguredDisplay display,
        string? token,
        string? certificatePin,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        string? presentedFingerprint = null;
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            presentedFingerprint = SamsungCertificatePins.Fingerprint(certificate);
            return string.IsNullOrWhiteSpace(certificatePin) ||
                SamsungCertificatePins.Matches(certificatePin, presentedFingerprint);
        };

        var uri = BuildRemoteUri(display, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(string.IsNullOrWhiteSpace(token) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(8));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(uri, timeout.Token);
            var hello = await ReceiveMessageAsync(socket, timeout.Token);
            var approvedToken = ParseConnectMessage(hello);
            stopwatch.Stop();
            if (display.Port == 8002 && presentedFingerprint is not null)
            {
                SamsungCertificatePins.Write(
                    _credentialStore,
                    presentedFingerprint,
                    display.HardwareId,
                    display.Address!);
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                _connectionTimes.Add(stopwatch.Elapsed);
            }

            if (!string.IsNullOrWhiteSpace(approvedToken))
            {
                _token = approvedToken;
                WriteCredential(display, approvedToken);
            }

            _socket = socket;
            _receiveCancellation = new CancellationTokenSource();
            _receiveTask = DrainMessagesAsync(socket, _receiveCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException(
                string.IsNullOrWhiteSpace(token)
                    ? "Pairing timed out. Try again and approve the request on the monitor."
                    : "The monitor did not respond. Check that it is on and connected to the same network.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task SendKeyCoreAsync(string key, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            method = "ms.remote.control",
            @params = new
            {
                Cmd = "Click",
                DataOfCmd = key,
                Option = "false",
                TypeOfRemote = "SendRemoteKey"
            }
        });
        await _socket!.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task CloseSocketAsync()
    {
        _receiveCancellation?.Cancel();
        if (_socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch (Exception exception) when (exception is WebSocketException or InvalidOperationException)
            {
            }
        }

        if (_receiveTask is not null)
        {
            await _receiveTask;
        }

        _receiveCancellation?.Dispose();
        _receiveCancellation = null;
        _receiveTask = null;
        _socket?.Dispose();
        _socket = null;
    }

    private static async Task<string> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("The monitor closed the remote connection.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static async Task DrainMessagesAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                await ReceiveMessageAsync(socket, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
        }
    }

    private static string? ParseConnectMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var eventName = root.TryGetProperty("event", out var eventElement)
            ? eventElement.GetString()
            : null;
        if (!string.Equals(eventName, "ms.channel.connect", StringComparison.Ordinal) &&
            !string.Equals(eventName, "ms.channel.ready", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The monitor did not approve the remote connection.");
        }

        return root.TryGetProperty("data", out var data) &&
               data.TryGetProperty("token", out var token) &&
               token.ValueKind == JsonValueKind.String
            ? token.GetString()
            : null;
    }

    private static Uri BuildRemoteUri(ConfiguredDisplay display, string? token)
    {
        var scheme = display.Port == 8002 ? "wss" : "ws";
        var name = WebUtility.UrlEncode(Convert.ToBase64String(Encoding.UTF8.GetBytes(RemoteName)));
        var tokenParameter = string.IsNullOrWhiteSpace(token)
            ? string.Empty
            : $"&token={WebUtility.UrlEncode(token)}";
        return new Uri(
            $"{scheme}://{display.Address}:{display.Port}/api/v2/channels/samsung.remote.control?name={name}{tokenParameter}");
    }

    private static void ValidateEndpoint(ConfiguredDisplay display)
    {
        if (!IPAddress.TryParse(display.Address, out var parsed) || !NetworkAddressHelper.IsPrivate(parsed))
        {
            throw new InvalidOperationException("Samsung network control is limited to displays on the local private network.");
        }

        if (display.Port is not (8001 or 8002))
        {
            throw new InvalidOperationException("The Samsung remote endpoint must use port 8001 or 8002.");
        }
    }

    private string? ReadCredential(ConfiguredDisplay display)
    {
        foreach (var target in CredentialTargets(display, CredentialPrefix))
        {
            var token = _credentialStore.Read(target);
            if (!string.IsNullOrWhiteSpace(token))
            {
                WriteCredential(display, token);
                return token;
            }
        }

        foreach (var legacyTarget in CredentialTargets(display, LegacyCredentialPrefix))
        {
            var token = _credentialStore.Read(legacyTarget);
            if (!string.IsNullOrWhiteSpace(token))
            {
                WriteCredential(display, token);
                return token;
            }
        }

        return null;
    }

    private void WriteCredential(ConfiguredDisplay display, string token)
    {
        foreach (var target in CredentialTargets(display, CredentialPrefix))
        {
            _credentialStore.Write(target, token);
        }
    }

    private void DeleteCredentials(ConfiguredDisplay display)
    {
        foreach (var target in CredentialTargets(display, CredentialPrefix))
        {
            _credentialStore.Delete(target);
        }
    }

    private static IReadOnlyList<string> CredentialTargets(ConfiguredDisplay display, string prefix)
    {
        var identities = new[]
        {
            display.HardwareId,
            display.Address
        };
        return identities
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Select(identity => $"{prefix}{identity}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool SameDisplay(ConfiguredDisplay left, ConfiguredDisplay? right) =>
        right is not null &&
        left.Port == right.Port &&
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Address, right.Address, StringComparison.OrdinalIgnoreCase);
}
