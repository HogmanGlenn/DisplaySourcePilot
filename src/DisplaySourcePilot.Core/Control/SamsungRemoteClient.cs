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
    private HttpMessageInvoker? _socketHttpClient;
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
            await OpenAuthorizedSocketAsync(display, progress, cancellationToken);
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
                await OpenAuthorizedSocketAsync(_display, null, cancellationToken);
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

    internal async Task OpenAuthorizedSocketAsync(
        ConfiguredDisplay display,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<string?, CancellationToken, Task>? openSocket = null)
    {
        _token = display.Port == 8002 ? ReadCredential(display) : null;
        var certificatePin = display.Port == 8002
            ? SamsungCertificatePins.Read(_credentialStore, display.HardwareId, display.Address!)
            : null;
        openSocket ??= (token, cancellation) => OpenSocketAsync(display, token, certificatePin, cancellation);
        progress?.Report(string.IsNullOrWhiteSpace(_token) && display.Port == 8002
            ? "Approve DisplaySourcePilot on the monitor."
            : "Connecting to the monitor...");
        try
        {
            await openSocket(_token, cancellationToken);
        }
        catch (SamsungAuthorizationException) when (!string.IsNullOrWhiteSpace(_token))
        {
            _token = null;
            progress?.Report("Approve DisplaySourcePilot on the monitor.");
            await openSocket(null, cancellationToken);
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
        socket.Options.Proxy = null;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false
        };
        var httpClient = new HttpMessageInvoker(handler);
        string? presentedFingerprint = null;
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
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
            await socket.ConnectAsync(uri, httpClient, timeout.Token);
            var approvedToken = await ReceiveConnectMessageAsync(socket, timeout.Token);
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

            if (display.Port == 8002 && !string.IsNullOrWhiteSpace(approvedToken))
            {
                _token = approvedToken;
                WriteCredential(display, approvedToken);
            }
            else if (display.Port == 8002 && string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "The monitor approved the connection but did not return a reusable pairing token.");
            }

            _socket = socket;
            _socketHttpClient = httpClient;
            _receiveCancellation = new CancellationTokenSource();
            _receiveTask = DrainMessagesAsync(socket, _receiveCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            httpClient.Dispose();
            throw new TimeoutException(
                string.IsNullOrWhiteSpace(token)
                    ? "Pairing timed out. Try again and approve the request on the monitor."
                    : "The monitor did not finish connecting. Check that it is on and approve DisplaySourcePilot if a request is shown.");
        }
        catch
        {
            socket.Dispose();
            httpClient.Dispose();
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
        _socketHttpClient?.Dispose();
        _socketHttpClient = null;
    }

    private static async Task<string> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
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

    internal static async Task<string?> ReceiveConnectMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReceiveMessageAsync(socket, cancellationToken);
            if (TryParseConnectMessage(message, out var token))
            {
                return token;
            }
        }
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

    internal static bool TryParseConnectMessage(string json, out string? token)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var eventName = root.TryGetProperty("event", out var eventElement)
            ? eventElement.GetString()
            : null;
        if (string.Equals(eventName, "ms.channel.unauthorized", StringComparison.Ordinal))
        {
            throw new SamsungAuthorizationException();
        }

        if (string.Equals(eventName, "ms.channel.timeOut", StringComparison.Ordinal))
        {
            throw new TimeoutException("The monitor timed out while approving the remote connection. Try again and approve the request on the monitor.");
        }

        if (string.Equals(eventName, "ms.error", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The monitor reported an error while connecting. Try again.");
        }

        if (!string.Equals(eventName, "ms.channel.connect", StringComparison.Ordinal))
        {
            token = null;
            return false;
        }

        token = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? ReadToken(data)
            : null;
        return true;
    }

    private static string? ReadToken(JsonElement data)
    {
        if (data.TryGetProperty("token", out var token) && !string.IsNullOrWhiteSpace(TokenValue(token)))
        {
            return TokenValue(token);
        }

        if (!data.TryGetProperty("clients", out var clients) || clients.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var connectionId = data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
        string? onlyClientToken = null;
        var tokenCount = 0;
        foreach (var client in clients.EnumerateArray())
        {
            if (client.ValueKind != JsonValueKind.Object ||
                !client.TryGetProperty("attributes", out var attributes) ||
                attributes.ValueKind != JsonValueKind.Object ||
                !attributes.TryGetProperty("token", out var clientToken) ||
                string.IsNullOrWhiteSpace(TokenValue(clientToken)))
            {
                continue;
            }

            var value = TokenValue(clientToken);
            if (!string.IsNullOrWhiteSpace(connectionId) &&
                client.TryGetProperty("id", out var clientId) &&
                clientId.ValueKind == JsonValueKind.String &&
                string.Equals(clientId.GetString(), connectionId, StringComparison.Ordinal))
            {
                return value;
            }

            onlyClientToken = value;
            tokenCount++;
        }

        return string.IsNullOrWhiteSpace(connectionId) && tokenCount == 1 ? onlyClientToken : null;
    }

    private static string? TokenValue(JsonElement token) => token.ValueKind switch
    {
        JsonValueKind.String => token.GetString(),
        JsonValueKind.Number => token.GetRawText(),
        _ => null
    };

    internal static Uri BuildRemoteUri(ConfiguredDisplay display, string? token)
    {
        var scheme = display.Port == 8002 ? "wss" : "ws";
        var name = WebUtility.UrlEncode(Convert.ToBase64String(Encoding.UTF8.GetBytes(RemoteName)));
        var tokenParameter = display.Port != 8002 || string.IsNullOrWhiteSpace(token)
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

internal sealed class SamsungAuthorizationException : InvalidOperationException
{
    public SamsungAuthorizationException()
        : base("The monitor rejected the saved remote authorization.")
    {
    }
}
