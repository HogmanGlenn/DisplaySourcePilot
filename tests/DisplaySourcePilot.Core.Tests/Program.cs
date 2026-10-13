using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Control;
using DisplaySourcePilot.Core.Discovery;
using DisplaySourcePilot.Core.Models;
using DisplaySourcePilot.Core.Security;

if (args is ["--audit-release", var releaseDirectory])
{
    return ReleaseAudit.Run(releaseDirectory);
}

if (args is ["--read-test-credential", var credentialTarget, var expectedCredential] &&
    credentialTarget.StartsWith("DisplaySourcePilot.Tests:", StringComparison.Ordinal))
{
    return new CredentialStore().Read(credentialTarget) == expectedCredential ? 0 : 1;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("DDC capabilities", TestDdcCapabilities),
    ("Samsung model profiles", TestSamsungProfiles),
    ("Remote route reversal", TestRouteReversal),
    ("Remote route validation", TestRouteValidation),
    ("Monitor shortcut identity", TestMonitorShortcutIdentity),
    ("Volatile DDC identity rejection", TestVolatileDdcIdentity),
    ("Unsafe Samsung route rejection", TestUnsafeSamsungRoute),
    ("Samsung command pacing", TestSamsungCommandPacing),
    ("Samsung pairing responses", TestSamsungPairingResponses),
    ("Samsung pairing event sequence", TestSamsungPairingEventSequence),
    ("Samsung token transport privacy", TestSamsungTokenTransport),
    ("Diagnostic privacy", TestDiagnosticPrivacy),
    ("Release privacy guard", TestReleasePrivacyGuard),
    ("Credential persistence across processes", TestCredentialPersistence),
    ("Saved token survives failed re-pairing", TestFailedPairingRetainsToken),
    ("Fresh user setup", TestFreshUserSetup),
    ("Window placement persistence", TestWindowPlacementPersistence),
    ("Window placement display recovery", TestWindowPlacementDisplayRecovery),
    ("Samsung address recovery and restart", TestSamsungAddressRecovery),
    ("Samsung recovery rejects another monitor", TestSamsungRecoveryRejectsAnotherMonitor),
    ("Samsung recovery cancellation", TestSamsungRecoveryCancellation),
    ("Timing calculation", TestTimingOptimizer),
    ("Manual Samsung address validation", TestManualSamsungAddressValidation),
    ("Hotkey settings", TestHotkeySettings),
    ("Invalid settings recovery", TestInvalidSettingsRecovery),
    ("Unsafe display settings recovery", TestUnsafeDisplaySettingsRecovery),
    ("Samsung timing settings recovery", TestSamsungTimingSettingsRecovery),
    ("Numeric enum settings recovery", TestNumericEnumSettingsRecovery),
    ("Concurrent settings writes", TestConcurrentSettingsWrites),
    ("Settings round trip", TestSettingsRoundTrip)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL  {test.Name}: {exception.Message}");
    }
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;

static Task TestDdcCapabilities()
{
    const string capabilities = "(prot(monitor)type(LCD)vcp(10 12 60(0f 11 12 1b) D6(01 04)))";
    var inputs = DdcCapabilitiesParser.ParseInputs(capabilities);
    AssertEqual(4, inputs.Count, "input count");
    AssertEqual("DisplayPort", inputs[0].Label, "DisplayPort label");
    AssertEqual(InputKind.Hdmi, inputs[1].Kind, "HDMI kind");
    AssertEqual((uint)0x12, inputs[2].VcpCode, "HDMI 2 code");
    return Task.CompletedTask;
}

static Task TestWindowPlacementPersistence()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        var store = new WindowPlacementStore(root);
        Assert(store.Load() is null, "a fresh user inherited window placement");
        var placement = new WindowPlacement(-1400, 160, 780, 720, 144, Maximized: true, UserResized: true);
        store.Save(placement);
        AssertEqual(placement, new WindowPlacementStore(root).Load(), "window placement after restart");
        Assert(!File.Exists(store.PathName + ".tmp"), "temporary placement file remained");
        File.WriteAllText(store.PathName, "{ broken");
        Assert(store.Load() is null, "corrupt placement did not recover");
        File.WriteAllText(store.PathName, """{"X":-32000,"Y":-32000,"Width":0,"Height":0,"Dpi":0}""");
        Assert(store.Load() is null, "invalid placement was restored");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    return Task.CompletedTask;
}

static Task TestWindowPlacementDisplayRecovery()
{
    var placement = new WindowPlacement(-1400, 160, 780, 720, 144, UserResized: true);
    AssertEqual(placement, placement.Restore(-1920, 0, 1920, 1080, 144, 630, 720), "negative secondary-monitor coordinates");
    var recovered = placement.Restore(0, 0, 1920, 1080, 96, 420, 480);
    AssertEqual(0, recovered.X, "disconnected-monitor recovery");
    AssertEqual(160, recovered.Y, "visible vertical position");
    AssertEqual(520, recovered.Width, "width after DPI change");
    AssertEqual(480, recovered.Height, "height after DPI change");
    Assert(recovered.UserResized, "restoration lost the user's resize preference");
    var huge = new WindowPlacement(int.MaxValue, int.MaxValue, 6000, 4000).Restore(0, 40, 1280, 680, 96, 420, 480);
    AssertEqual(0, huge.X, "oversized window horizontal recovery");
    AssertEqual(40, huge.Y, "work-area top edge");
    AssertEqual(1280, huge.Width, "window exceeded available width");
    AssertEqual(680, huge.Height, "window exceeded available height");
    var small = placement.Restore(0, 0, 320, 240, 192, 840, 960);
    AssertEqual(320, small.Width, "small-display width");
    AssertEqual(240, small.Height, "small-display height");
    return Task.CompletedTask;
}

static Task TestSamsungProfiles()
{
    var profile = SamsungSourceProfiles.Find("LS32DG802SUXEN");
    Assert(profile is not null, "G80SD profile was not matched");
    AssertEqual("HDMI 2", profile!.Hdmi.Label, "HDMI label");
    AssertEqual("KEY_UP", profile.Hdmi.Route!.Keys.Single(), "HDMI route");
    Assert(SamsungSourceProfiles.Find("UNKNOWN") is null, "unknown model matched a profile");
    return Task.CompletedTask;
}

static Task TestRouteReversal()
{
    var route = new RemoteRoute(["KEY_UP", "KEY_RIGHT", "KEY_RIGHT"]);
    var reversed = route.Reverse();
    AssertEqual("KEY_LEFT,KEY_LEFT,KEY_DOWN", string.Join(',', reversed.Keys), "reverse route");
    return Task.CompletedTask;
}

static Task TestRouteValidation()
{
    Assert(new RemoteRoute(["KEY_UP", "KEY_RIGHT"]).IsSafeNavigation, "a direct route was rejected");
    Assert(!new RemoteRoute(["KEY_UP", "KEY_DOWN"]).IsSafeNavigation, "an immediate reversal was accepted");
    Assert(!new RemoteRoute([]).IsSafeNavigation, "an empty route was accepted");
    return Task.CompletedTask;
}

static Task TestMonitorShortcutIdentity()
{
    var display = new ConfiguredDisplay
    {
        Id = "samsung:monitor-1",
        Name = "Test display",
        Method = ControlMethod.SamsungTizen,
        Address = "192.168.1.2",
        Port = 8002,
        Hdmi = new InputSource("hdmi", "HDMI", InputKind.Hdmi, Route: RemoteRoute.Up()),
        DisplayPort = new InputSource("displayport", "DisplayPort", InputKind.DisplayPort, Route: RemoteRoute.Down())
    };
    var other = new ConfiguredDisplay
    {
        Id = "samsung:monitor-2",
        Name = display.Name,
        Method = display.Method,
        Address = display.Address,
        Port = display.Port,
        Hdmi = display.Hdmi,
        DisplayPort = display.DisplayPort
    };

    AssertEqual(64, display.ShortcutToken.Length, "shortcut token length");
    Assert(display.ShortcutToken.All(character => char.IsAsciiHexDigitLower(character) || char.IsDigit(character)), "shortcut token format");
    Assert(display.ShortcutToken != other.ShortcutToken, "different monitors received the same shortcut token");
    return Task.CompletedTask;
}

static Task TestVolatileDdcIdentity()
{
    var display = new ConfiguredDisplay
    {
        Id = @"ddc:\\.\DISPLAY1:0",
        Name = "Test display",
        Method = ControlMethod.Ddc,
        HardwareId = @"\\.\DISPLAY1",
        Hdmi = new InputSource("hdmi", "HDMI", InputKind.Hdmi, 0x11),
        DisplayPort = new InputSource("displayport", "DisplayPort", InputKind.DisplayPort, 0x0f)
    };

    Assert(!display.IsValid, "a volatile Windows display number was accepted as a durable monitor identity");
    return Task.CompletedTask;
}

static async Task TestUnsafeSamsungRoute()
{
    await using var remote = new SamsungRemoteClient();
    var controller = new SamsungController(remote);
    var unsafeInput = new InputSource(
        "hdmi",
        "HDMI",
        InputKind.Hdmi,
        Route: new RemoteRoute(["KEY_HOME"]));
    var display = new ConfiguredDisplay
    {
        Id = "samsung:test",
        Name = "Test display",
        Method = ControlMethod.SamsungTizen,
        Address = "192.168.1.2",
        Port = 8002,
        Hdmi = unsafeInput,
        DisplayPort = new InputSource(
            "displayport",
            "DisplayPort",
            InputKind.DisplayPort,
            Route: RemoteRoute.Down()),
        LastKnownInput = InputKind.DisplayPort
    };

    try
    {
        await controller.SwitchAsync(display, unsafeInput);
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException("an unsafe remote key was accepted");
}

static async Task TestSamsungCommandPacing()
{
    var remote = new RecordingSamsungRemote();
    var delays = new List<TimeSpan>();
    var controller = new SamsungController(
        remote,
        (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
    var input = new InputSource("hdmi", "HDMI", InputKind.Hdmi, Route: RemoteRoute.Up(2));
    var display = new ConfiguredDisplay
    {
        Id = "samsung:test",
        Name = "Test display",
        Method = ControlMethod.SamsungTizen,
        Address = "192.168.1.2",
        Port = 8002,
        Hdmi = input,
        DisplayPort = new InputSource("displayport", "DisplayPort", InputKind.DisplayPort, Route: RemoteRoute.Down()),
        MenuDelayMilliseconds = 230,
        LastKnownInput = InputKind.DisplayPort
    };

    await controller.SwitchAsync(display, input);

    AssertEqual("KEY_SOURCE,KEY_UP,KEY_UP,KEY_ENTER", string.Join(',', remote.Keys), "Samsung command order");
    AssertEqual("700,300,300,900", string.Join(',', delays.Select(delay => delay.TotalMilliseconds)), "Samsung command delays");
}

static Task TestSamsungPairingResponses()
{
    const string directTokenResponse =
        """{"event":"ms.channel.connect","data":{"id":"client-1","token":"12345678"}}""";
    Assert(
        SamsungRemoteClient.TryParseConnectMessage(directTokenResponse, out var directToken),
        "direct token response was not accepted");
    AssertEqual("12345678", directToken, "direct pairing token");

    const string clientTokenResponse =
        """
        {
          "event": "ms.channel.connect",
          "data": {
            "id": "current-client",
            "clients": [
              { "id": "other-client", "attributes": { "token": "wrong-token" } },
              { "id": "current-client", "attributes": { "token": "87654321" } }
            ]
          }
        }
        """;
    Assert(
        SamsungRemoteClient.TryParseConnectMessage(clientTokenResponse, out var clientToken),
        "client token response was not accepted");
    AssertEqual("87654321", clientToken, "current client pairing token");

    Assert(
        !SamsungRemoteClient.TryParseConnectMessage("""{"event":"ms.channel.ready"}""", out var readyToken),
        "channel-ready response completed pairing");
    AssertEqual<string?>(null, readyToken, "channel-ready pairing token");

    Assert(
        !SamsungRemoteClient.TryParseConnectMessage("""{"event":"ms.channel.clientConnect"}""", out _),
        "a transient channel event completed pairing");

    Assert(
        SamsungRemoteClient.TryParseConnectMessage("""{"event":"ms.channel.connect","data":{"token":12345678}}""", out var numericToken),
        "numeric token response was not accepted");
    AssertEqual("12345678", numericToken, "numeric pairing token");

    Assert(
        SamsungRemoteClient.TryParseConnectMessage(
            """{"event":"ms.channel.connect","data":{"id":"current-client","clients":[{"id":"other-client","attributes":{"token":"wrong-token"}}]}}""",
            out var otherToken),
        "connection without a current-client token was not accepted");
    AssertEqual<string?>(null, otherToken, "another client's token must not be reused");

    Assert(
        SamsungRemoteClient.TryParseConnectMessage(
            """{"event":"ms.channel.connect","data":{"clients":[{"attributes":{"token":"only-token"}}]}}""",
            out var onlyToken),
        "single-client response was not accepted");
    AssertEqual("only-token", onlyToken, "single client token without connection id");

    try
    {
        SamsungRemoteClient.TryParseConnectMessage("""{"event":"ms.error"}""", out _);
    }
    catch (SamsungAuthorizationException)
    {
        throw new InvalidOperationException("a monitor error was classified as token rejection");
    }
    catch (InvalidOperationException)
    {
    }

    try
    {
        SamsungRemoteClient.TryParseConnectMessage("""{"event":"ms.channel.unauthorized"}""", out _);
    }
    catch (SamsungAuthorizationException)
    {
        return Task.CompletedTask;
    }

    throw new InvalidOperationException("an unauthorized response was not identified as token rejection");
}

static async Task TestSamsungPairingEventSequence()
{
    using var socket = new ScriptedWebSocket(
        """{"event":"ms.channel.ready"}""",
        """{"event":"ms.channel.clientConnect"}""",
        """{"event":"ms.channel.connect","data":{"id":"current-client","clients":[{"id":"current-client","attributes":{"token":"saved-token"}}]}}""");
    var token = await SamsungRemoteClient.ReceiveConnectMessageAsync(socket, CancellationToken.None);
    AssertEqual("saved-token", token, "token after intermediate handshake events");
    AssertEqual(3, socket.MessagesRead, "handshake event count");

    using var cancelledSocket = new ScriptedWebSocket("""{"event":"ms.channel.ready"}""");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await SamsungRemoteClient.ReceiveConnectMessageAsync(cancelledSocket, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        AssertEqual(0, cancelledSocket.MessagesRead, "cancelled pairing consumed a message");
        return;
    }

    throw new InvalidOperationException("pairing ignored cancellation");
}

static async Task TestSamsungAddressRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        var store = new SettingsStore(root);
        var original = SamsungRecoverySettings();
        await store.SaveAsync(original);
        var remote = new RecordingSamsungRemote { OfflineAddress = "192.168.1.2" };
        var discovery = new RecordingSamsungDiscovery(
            new SamsungIdentity("other", "Other monitor", "LS32DG80", "192.168.1.3", true, []),
            new SamsungIdentity("test", "Test display", "LS32DG80", "192.168.1.6", true, []));
        await using (var service = new DisplaySourcePilotService(store, remote, discovery))
        {
            await service.InitializeAsync();
            AssertEqual("192.168.1.2,192.168.1.6", string.Join(',', remote.Addresses), "recovered connection attempts");
            AssertEqual(1, discovery.Calls, "recovery scan count");
            AssertEqual("192.168.1.6", service.Settings.Display!.Address, "recovered address");
            AssertEqual(original.Display!.ShortcutToken, service.Settings.Display.ShortcutToken, "shortcut identity after recovery");
            AssertEqual(8002, service.Settings.Display.Port, "secure port after recovery");
            AssertEqual(InputKind.DisplayPort, service.Settings.Display.LastKnownInput, "current input after recovery");
            AssertEqual("KEY_UP", service.Settings.Display.Hdmi.Route!.Keys.Single(), "saved route after recovery");
            AssertEqual("Ctrl+Shift+J", service.Settings.Hotkeys.Hdmi.DisplayText, "hotkeys after recovery");
            Assert(service.Settings.RunAtStartup && service.Settings.KeepRunning, "startup preferences changed");
        }

        var restartRemote = new RecordingSamsungRemote();
        var restartDiscovery = new RecordingSamsungDiscovery();
        await using var restarted = new DisplaySourcePilotService(new SettingsStore(root), restartRemote, restartDiscovery);
        await restarted.InitializeAsync();
        AssertEqual("192.168.1.6", restartRemote.Addresses.Single(), "address after restart");
        AssertEqual(0, restartDiscovery.Calls, "restart unnecessarily rediscovered monitor");

        restartRemote.OfflineAddress = "192.168.1.6";
        restartDiscovery.Identities = [new SamsungIdentity("test", "Test display", "LS32DG80", "192.168.1.8", true, [])];
        await restarted.SwitchAsync(InputKind.Hdmi);
        AssertEqual("192.168.1.8", restarted.Settings.Display!.Address, "address recovered during switching");
        AssertEqual(InputKind.Hdmi, restarted.Settings.Display.LastKnownInput, "completed switch state");
        AssertEqual("KEY_SOURCE,KEY_UP,KEY_ENTER", string.Join(',', restartRemote.Keys), "recovered switch command order");
        var saved = await store.LoadAsync();
        AssertEqual("192.168.1.8", saved.Display!.Address, "persisted address after switch");
        AssertEqual(InputKind.Hdmi, saved.Display.LastKnownInput, "persisted completed switch state");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static Task TestSamsungTokenTransport()
{
    var secure = SamsungRecoverySettings().Display!;
    var secureUri = SamsungRemoteClient.BuildRemoteUri(secure, "fake token&value");
    AssertEqual("wss", secureUri.Scheme, "pairing token transport");
    Assert(secureUri.Query.Contains("token=fake+token%26value", StringComparison.Ordinal), "secure token was not encoded");
    var legacy = new ConfiguredDisplay
    {
        Id = secure.Id,
        Name = secure.Name,
        Method = secure.Method,
        Address = secure.Address,
        Port = 8001,
        Hdmi = secure.Hdmi,
        DisplayPort = secure.DisplayPort
    };
    var legacyUri = SamsungRemoteClient.BuildRemoteUri(legacy, "fake-secret-token");
    AssertEqual("ws", legacyUri.Scheme, "legacy transport");
    Assert(!legacyUri.Query.Contains("token", StringComparison.OrdinalIgnoreCase), "a pairing token was sent without TLS");
    return Task.CompletedTask;
}

static Task TestDiagnosticPrivacy()
{
    var values = new[]
    {
        "?token=example-query-token&name=client",
        "\"token\":\"example-json-token\"",
        "'access_token': 'example-access-token'",
        "refresh_token=example-refresh-token",
        "pairingToken: 87654321",
        "password=example-password",
        "secret=example-secret",
        "Connection refused (192.168.50.60:8002)",
        "uuid:01234567-89ab-cdef-0123-456789abcdef",
        @"Access denied to 'C:\Users\Private Tester\AppData\settings.json'",
        "File C:/Users/Private Tester/settings.json failed",
        Environment.UserName
    };
    var sensitive = new[]
    {
        "example-query-token", "example-json-token", "example-access-token", "example-refresh-token",
        "87654321", "example-password", "example-secret", "192.168.50.60",
        "01234567-89ab-cdef-0123-456789abcdef", "Private Tester", Environment.UserName
    };
    var redacted = DiagnosticRedactor.Redact(string.Join('\n', values));
    foreach (var value in sensitive)
    {
        Assert(!redacted.Contains(value, StringComparison.OrdinalIgnoreCase), "a private value remained in diagnostics");
    }

    Assert(redacted.Contains("Connection refused", StringComparison.Ordinal), "redaction removed useful failure context");
    Assert(redacted.Contains("&name=client", StringComparison.Ordinal), "redaction damaged adjacent query parameters");
    AssertEqual("Monitor connected.", DiagnosticRedactor.Redact("Monitor connected."), "normal status redaction");
    return Task.CompletedTask;
}

static Task TestReleasePrivacyGuard()
{
    const string privateValue = "example-private-pairing-value";
    foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
    {
        var payload = new byte[] { 0xff }.Concat(encoding.GetBytes(privateValue)).ToArray();
        Assert(ReleaseAudit.ContainsPrivateValue(payload, [privateValue]), "release guard missed an encoded private value");
        Assert(!ReleaseAudit.ContainsPrivateValue(payload, ["another-private-value"]), "release guard matched an absent value");
    }

    foreach (var name in new[] { "settings.json", "window.json", "token.json", "app.log", "private.key", "DisplaySourcePilot.Core.pdb" })
    {
        Assert(!ReleaseAudit.IsAllowedFileName(name), "release accepted a private or debug file");
    }

    Assert(ReleaseAudit.IsAllowedFileName("DisplaySourcePilot.runtimeconfig.json"), "release rejected its runtime metadata");
    return Task.CompletedTask;
}

static async Task TestCredentialPersistence()
{
    var target = $"DisplaySourcePilot.Tests:{Guid.NewGuid():N}";
    const string value = "public-test-value-ä-漢";
    var store = new CredentialStore();
    try
    {
        store.Write(target, value);
        AssertEqual(value, new CredentialStore().Read(target), "stored credential round trip");
        var runtimeRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
        var start = new ProcessStartInfo(Path.Combine(runtimeRoot, "dotnet.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--read-test-credential");
        start.ArgumentList.Add(target);
        start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The credential test process did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        AssertEqual(0, process.ExitCode, "a fresh process could not read the persisted credential");
    }
    finally
    {
        store.Delete(target);
    }
}

static async Task TestFreshUserSetup()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        var remote = new RecordingSamsungRemote();
        var discovery = new RecordingSamsungDiscovery();
        await using var service = new DisplaySourcePilotService(new SettingsStore(root), remote, discovery);
        await service.InitializeAsync();
        Assert(!service.IsReady && service.Settings.Display is null, "a fresh user inherited a configured monitor");
        AssertEqual(0, remote.Addresses.Count, "a fresh user contacted a developer's monitor");
        AssertEqual(0, discovery.Calls, "unconfigured initialization attempted saved-address recovery");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static async Task TestFailedPairingRetainsToken()
{
    var hardwareId = $"test:{Guid.NewGuid():N}";
    var target = $"DisplaySourcePilot:Samsung:{hardwareId}";
    const string token = "public-test-cached-token";
    var store = new CredentialStore();
    try
    {
        store.Write(target, token);
        await using var remote = new SamsungRemoteClient(store);
        var inputs = SamsungRecoverySettings().Display!;
        var display = new ConfiguredDisplay
        {
            Id = $"samsung:{hardwareId}",
            Name = "Test display",
            HardwareId = hardwareId,
            Method = ControlMethod.SamsungTizen,
            Port = 8002,
            Hdmi = inputs.Hdmi,
            DisplayPort = inputs.DisplayPort
        };
        var attempts = 0;
        try
        {
            await remote.OpenAuthorizedSocketAsync(display, null, CancellationToken.None, (saved, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    AssertEqual(token, saved, "saved token was not tried first");
                    throw new SamsungAuthorizationException();
                }

                AssertEqual<string?>(null, saved, "re-pairing reused the rejected token");
                AssertEqual(token, new CredentialStore().Read(target), "token was deleted before replacement approval");
                throw new TimeoutException("Approval was not completed");
            });
        }
        catch (TimeoutException)
        {
            AssertEqual(2, attempts, "authorization retry count");
            AssertEqual(token, new CredentialStore().Read(target), "failed re-pairing lost the saved token");
        }

        var connectionFailed = false;
        try
        {
            await remote.OpenAuthorizedSocketAsync(display, null, CancellationToken.None, (saved, _) =>
            {
                AssertEqual(token, saved, "retry did not reuse the persisted token");
                throw new WebSocketException("Connection refused");
            });
        }
        catch (WebSocketException)
        {
            connectionFailed = true;
        }

        Assert(connectionFailed, "connection failure was swallowed");
        AssertEqual(token, new CredentialStore().Read(target), "connection failure deleted the token");
    }
    finally
    {
        store.Delete(target);
    }
}

static async Task TestSamsungRecoveryRejectsAnotherMonitor()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        var store = new SettingsStore(root);
        await store.SaveAsync(SamsungRecoverySettings());
        var remote = new RecordingSamsungRemote { OfflineAddress = "192.168.1.2" };
        var discovery = new RecordingSamsungDiscovery(
            new SamsungIdentity("other", "Test display", "LS32DG80", "192.168.1.6", true, []));
        await using var service = new DisplaySourcePilotService(store, remote, discovery);
        await service.InitializeAsync();
        try
        {
            await service.SwitchAsync(InputKind.Hdmi);
        }
        catch (InvalidOperationException exception) when (exception.InnerException is WebSocketException)
        {
            Assert(remote.Addresses.All(address => address == "192.168.1.2"), "recovery connected to another monitor");
            AssertEqual(0, remote.Keys.Count, "failed recovery sent source commands");
            var saved = await store.LoadAsync();
            AssertEqual("192.168.1.2", saved.Display!.Address, "failed recovery changed saved address");
            AssertEqual(InputKind.DisplayPort, saved.Display.LastKnownInput, "failed recovery lost known input");
            return;
        }

        throw new InvalidOperationException("recovery accepted a different monitor with the same name and model");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static async Task TestSamsungRecoveryCancellation()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        var store = new SettingsStore(root);
        await store.SaveAsync(SamsungRecoverySettings());
        var remote = new RecordingSamsungRemote { OfflineAddress = "192.168.1.2" };
        var discovery = new RecordingSamsungDiscovery { Cancel = true };
        await using var service = new DisplaySourcePilotService(store, remote, discovery);
        try
        {
            await service.InitializeAsync();
        }
        catch (OperationCanceledException)
        {
            AssertEqual(0, remote.Keys.Count, "cancelled recovery sent source commands");
            AssertEqual("192.168.1.2", (await store.LoadAsync()).Display!.Address, "cancelled recovery changed saved address");
            return;
        }

        throw new InvalidOperationException("monitor discovery cancellation was swallowed");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static AppSettings SamsungRecoverySettings() => new()
{
    RunAtStartup = true,
    KeepRunning = true,
    Hotkeys = new HotkeySettings
    {
        Hdmi = new HotkeyBinding(HotkeyModifiers.ControlShift, "J"),
        DisplayPort = new HotkeyBinding(HotkeyModifiers.ControlShift, "K")
    },
    Display = new ConfiguredDisplay
    {
        Id = "samsung:test",
        Name = "Test display",
        Method = ControlMethod.SamsungTizen,
        Address = "192.168.1.2",
        Port = 8002,
        HardwareId = "test",
        Model = "LS32DG80",
        Hdmi = new InputSource("hdmi2", "HDMI 2", InputKind.Hdmi, Route: RemoteRoute.Up()),
        DisplayPort = new InputSource("dp", "DisplayPort", InputKind.DisplayPort, Route: RemoteRoute.Down()),
        MenuDelayMilliseconds = 700,
        LastKnownInput = InputKind.DisplayPort
    }
};

static Task TestTimingOptimizer()
{
    var profile = SamsungSourceProfiles.Find("LS32DG80")!;
    var delay = TimingOptimizer.CalculateMenuDelay(
        profile,
        [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30)]);
    AssertEqual(710, delay, "calculated delay");
    AssertEqual(700, TimingOptimizer.CalculateMenuDelay(profile, []), "default delay");
    AssertEqual(
        700,
        TimingOptimizer.CalculateMenuDelay(650, [TimeSpan.FromMilliseconds(20)], 700, 1200),
        "generic Samsung delay");
    return Task.CompletedTask;
}

static Task TestHotkeySettings()
{
    var settings = new HotkeySettings
    {
        Hdmi = new HotkeyBinding(HotkeyModifiers.ControlShift, "J"),
        DisplayPort = new HotkeyBinding(HotkeyModifiers.AltShift, "K")
    };
    Assert(settings.IsValid, "valid shortcuts were rejected");
    AssertEqual("Ctrl+Shift+J", settings.Hdmi.DisplayText, "shortcut label");

    settings.DisplayPort = settings.Hdmi;
    Assert(!settings.IsValid, "duplicate shortcuts were accepted");
    settings.DisplayPort = new HotkeyBinding(settings.Hdmi.Modifiers, "j");
    Assert(!settings.IsValid, "case-insensitive duplicate shortcuts were accepted");
    return Task.CompletedTask;
}

static async Task TestManualSamsungAddressValidation()
{
    try
    {
        await new DisplayDiscoveryService().DiscoverSamsungAtAddressAsync("8.8.8.8");
    }
    catch (ArgumentException)
    {
        return;
    }

    throw new InvalidOperationException("a public IP address was accepted");
}

static async Task TestInvalidSettingsRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{ not valid json");
        var settings = await new SettingsStore(root).LoadAsync();
        Assert(settings.Display is null, "invalid settings did not fall back to setup");
        Assert(!File.Exists(Path.Combine(root, "settings.json")), "invalid settings file was not moved");
        AssertEqual(1, Directory.GetFiles(root, "settings.invalid-*.json").Length, "invalid settings backup count");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static async Task TestSettingsRoundTrip()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        var store = new SettingsStore(root);
        var settings = new AppSettings
        {
            RunAtStartup = false,
            KeepRunning = false,
            Hotkeys = new HotkeySettings
            {
                Hdmi = new HotkeyBinding(HotkeyModifiers.ControlShift, "J"),
                DisplayPort = new HotkeyBinding(HotkeyModifiers.ControlShift, "K")
            },
            Display = new ConfiguredDisplay
            {
                Id = "samsung:test",
                Name = "Test display",
                Method = ControlMethod.SamsungTizen,
                Address = "192.168.1.2",
                Port = 8002,
                Model = "LS32DG80",
                HardwareId = "test",
                Hdmi = new InputSource("hdmi2", "HDMI 2", InputKind.Hdmi, Route: RemoteRoute.Up()),
                DisplayPort = new InputSource("dp", "DisplayPort", InputKind.DisplayPort, Route: RemoteRoute.Down()),
                MenuDelayMilliseconds = 240,
                LastKnownInput = InputKind.DisplayPort
            }
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        AssertEqual(false, loaded.RunAtStartup, "startup setting");
        AssertEqual(false, loaded.KeepRunning, "headless mode setting");
        AssertEqual("Ctrl+Shift+J", loaded.Hotkeys.Hdmi.DisplayText, "serialized hotkey");
        AssertEqual("KEY_UP", loaded.Display!.Hdmi.Route!.Keys.Single(), "serialized route");
        AssertEqual(700, loaded.Display.MenuDelayMilliseconds, "serialized delay");
        AssertEqual(InputKind.DisplayPort, loaded.Display.LastKnownInput, "last known input");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static async Task TestUnsafeDisplaySettingsRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        var store = new SettingsStore(root);
        await store.SaveAsync(new AppSettings
        {
            RunAtStartup = false,
            KeepRunning = false,
            Display = new ConfiguredDisplay
            {
                Id = "samsung:test",
                Name = "Unsafe display",
                Method = ControlMethod.SamsungTizen,
                Address = "192.168.1.2",
                Port = 8002,
                Hdmi = new InputSource(
                    "hdmi",
                    "HDMI",
                    InputKind.Hdmi,
                    Route: new RemoteRoute(["KEY_HOME"])),
                DisplayPort = new InputSource("dp", "DisplayPort", InputKind.DisplayPort, Route: RemoteRoute.Down()),
                MenuDelayMilliseconds = 50_000,
                LastKnownInput = InputKind.DisplayPort
            }
        });

        var loaded = await store.LoadAsync();
        Assert(loaded.Display is null, "unsafe display settings were accepted");
        AssertEqual(false, loaded.RunAtStartup, "startup preference was discarded");
        AssertEqual(false, loaded.KeepRunning, "headless preference was discarded");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static async Task TestSamsungTimingSettingsRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        var store = new SettingsStore(root);
        await store.SaveAsync(new AppSettings
        {
            Display = new ConfiguredDisplay
            {
                Id = "samsung:test",
                Name = "Test display",
                Method = ControlMethod.SamsungTizen,
                Address = "192.168.1.2",
                Port = 8002,
                Hdmi = new InputSource("hdmi", "HDMI", InputKind.Hdmi, Route: RemoteRoute.Up()),
                DisplayPort = new InputSource("dp", "DisplayPort", InputKind.DisplayPort, Route: RemoteRoute.Down()),
                MenuDelayMilliseconds = 50_000,
                LastKnownInput = InputKind.DisplayPort
            }
        });

        var loaded = await store.LoadAsync();
        AssertEqual(1200, loaded.Display!.MenuDelayMilliseconds, "Samsung menu delay was not clamped");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static async Task TestNumericEnumSettingsRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "settings.json"),
            """
            {
              "Version": 1,
              "Hotkeys": {
                "Hdmi": { "Modifiers": 0, "Key": "H" },
                "DisplayPort": { "Modifiers": "ControlAlt", "Key": "P" }
              }
            }
            """);

        var settings = await new SettingsStore(root).LoadAsync();
        AssertEqual("Ctrl+Alt+H", settings.Hotkeys.Hdmi.DisplayText, "numeric enum settings fallback");
        AssertEqual(1, Directory.GetFiles(root, "settings.invalid-*.json").Length, "numeric enum backup count");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static async Task TestConcurrentSettingsWrites()
{
    var root = Path.Combine(Path.GetTempPath(), $"DisplaySourcePilot.Tests.{Guid.NewGuid():N}");
    try
    {
        var store = new SettingsStore(root);
        var writes = Enumerable.Range(0, 12)
            .Select(index => store.SaveAsync(new AppSettings
            {
                RunAtStartup = index % 2 == 0,
                KeepRunning = index % 3 == 0
            }));
        await Task.WhenAll(writes);

        var loaded = await store.LoadAsync();
        AssertEqual(AppSettings.CurrentVersion, loaded.Version, "settings version after concurrent writes");
        Assert(!File.Exists(store.SettingsPath + ".tmp"), "temporary settings file remained after concurrent writes");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}

sealed class RecordingSamsungRemote : ISamsungRemoteControl
{
    public List<string> Keys { get; } = [];

    public IReadOnlyList<TimeSpan> ConnectionTimes => [];

    public List<string> Addresses { get; } = [];

    public string? OfflineAddress { get; set; }

    public Task ConnectAsync(
        ConfiguredDisplay display,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Addresses.Add(display.Address!);
        return display.Address == OfflineAddress
            ? Task.FromException(new WebSocketException("Connection refused"))
            : Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task SendKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        Keys.Add(key);
        return Task.CompletedTask;
    }
}
sealed class RecordingSamsungDiscovery(params SamsungIdentity[] identities) : ISamsungNetworkDiscovery
{
    public IReadOnlyList<SamsungIdentity> Identities { get; set; } = identities;

    public int Calls { get; private set; }

    public bool Cancel { get; init; }

    public Task<IReadOnlyList<SamsungIdentity>> DiscoverAsync(
        IProgress<DiscoveryStatus>? progress = null,
        IEnumerable<string>? preferredAddresses = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        return Cancel
            ? Task.FromCanceled<IReadOnlyList<SamsungIdentity>>(new CancellationToken(true))
            : Task.FromResult(Identities);
    }
}

sealed class ScriptedWebSocket(params string[] messages) : WebSocket
{
    public int MessagesRead { get; private set; }

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override void Dispose() { }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MessagesRead >= messages.Length)
        {
            throw new InvalidOperationException("pairing read beyond the scripted handshake");
        }

        var bytes = Encoding.UTF8.GetBytes(messages[MessagesRead++]);
        bytes.AsSpan().CopyTo(buffer.AsSpan());
        return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("a handshake test must not send commands");
}
