using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Control;
using DisplaySourcePilot.Core.Discovery;
using DisplaySourcePilot.Core.Models;

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

    public Task SendKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        Keys.Add(key);
        return Task.CompletedTask;
    }
}
