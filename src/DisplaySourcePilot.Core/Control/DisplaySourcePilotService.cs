using System.Net.WebSockets;
using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Control;

public sealed class DisplaySourcePilotService : IAsyncDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly DdcController _ddcController = new();
    private readonly SamsungRemoteClient _samsungRemote = new();
    private readonly SamsungController _samsungController;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private AppSettings _settings = new();

    public DisplaySourcePilotService(SettingsStore? settingsStore = null)
    {
        _settingsStore = settingsStore ?? new SettingsStore();
        _samsungController = new SamsungController(_samsungRemote);
    }

    public AppSettings Settings => _settings;

    public bool IsReady => _settings.Display is not null;

    public async Task InitializeAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _settings = await _settingsStore.LoadAsync(cancellationToken);
        if (_settings.Display?.Method == ControlMethod.SamsungTizen)
        {
            try
            {
                await _samsungRemote.ConnectAsync(_settings.Display, progress, cancellationToken);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or WebSocketException or TimeoutException or InvalidOperationException)
            {
                progress?.Report("Monitor offline. It will reconnect when you switch.");
            }
        }
    }

    public async Task ConfigureAsync(
        DisplayDevice device,
        InputSource hdmi,
        InputSource displayPort,
        IProgress<string>? progress = null,
        InputKind? currentInput = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var configured = new ConfiguredDisplay
            {
                Id = device.Id,
                Name = device.Name,
                Method = device.Method,
                Address = device.Address,
                Port = device.Port,
                Model = device.Model,
                HardwareId = device.HardwareId,
                Hdmi = hdmi,
                DisplayPort = displayPort,
                MenuDelayMilliseconds = device.SuggestedMenuDelayMilliseconds,
                LastKnownInput = device.Method == ControlMethod.SamsungTizen ? currentInput : null
            };

            if (configured.Method == ControlMethod.SamsungTizen)
            {
                await _samsungRemote.ConnectAsync(configured, progress, cancellationToken);
                var profile = SamsungSourceProfiles.Find(configured.Model ?? string.Empty);
                if (_samsungRemote.ConnectionTimes.Count > 0)
                {
                    configured.MenuDelayMilliseconds = profile is null
                        ? TimingOptimizer.CalculateMenuDelay(650, _samsungRemote.ConnectionTimes, 700, 1200)
                        : TimingOptimizer.CalculateMenuDelay(profile, _samsungRemote.ConnectionTimes);
                }
            }

            var previous = _settings.Display;
            _settings.Display = configured;
            try
            {
                await _settingsStore.SaveAsync(_settings, cancellationToken);
            }
            catch
            {
                _settings.Display = previous;
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SwitchAsync(InputKind input, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var display = _settings.Display ?? throw new InvalidOperationException("DisplaySourcePilot has not been set up.");
            var source = input switch
            {
                InputKind.Hdmi => display.Hdmi,
                InputKind.DisplayPort => display.DisplayPort,
                _ => throw new ArgumentOutOfRangeException(nameof(input))
            };

            if (display.Method == ControlMethod.Ddc)
            {
                await _ddcController.SwitchAsync(display, source, cancellationToken);
                return;
            }

            if (display.LastKnownInput is null)
            {
                throw new CurrentInputUnknownException();
            }

            var previousInput = display.LastKnownInput;
            display.LastKnownInput = null;
            try
            {
                await _settingsStore.SaveAsync(_settings, cancellationToken);
            }
            catch
            {
                display.LastKnownInput = previousInput;
                throw;
            }

            if (previousInput == input)
            {
                throw new CurrentInputUnknownException();
            }

            try
            {
                await _samsungRemote.ConnectAsync(display, cancellationToken: cancellationToken);
            }
            catch
            {
                display.LastKnownInput = previousInput;
                try
                {
                    await _settingsStore.SaveAsync(_settings, CancellationToken.None);
                }
                catch
                {
                    display.LastKnownInput = null;
                }

                throw;
            }

            await _samsungController.SwitchAsync(display, source, cancellationToken);
            display.LastKnownInput = input;
            try
            {
                await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            }
            catch
            {
                display.LastKnownInput = null;
                throw;
            }

        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SetCurrentInputAsync(InputKind input, CancellationToken cancellationToken = default)
    {
        if (input is not (InputKind.Hdmi or InputKind.DisplayPort))
        {
            throw new ArgumentOutOfRangeException(nameof(input));
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var display = _settings.Display ?? throw new InvalidOperationException("DisplaySourcePilot has not been set up.");
            if (display.Method != ControlMethod.SamsungTizen)
            {
                throw new InvalidOperationException("Input confirmation is only required for Samsung network monitors.");
            }

            var previous = display.LastKnownInput;
            display.LastKnownInput = input;
            try
            {
                await _settingsStore.SaveAsync(_settings, cancellationToken);
            }
            catch
            {
                display.LastKnownInput = previous;
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RequireCurrentInputConfirmationAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var display = _settings.Display ?? throw new InvalidOperationException("DisplaySourcePilot has not been set up.");
            if (display.Method != ControlMethod.SamsungTizen)
            {
                throw new InvalidOperationException("Input confirmation is only required for Samsung network monitors.");
            }

            var previous = display.LastKnownInput;
            display.LastKnownInput = null;
            try
            {
                await _settingsStore.SaveAsync(_settings, cancellationToken);
            }
            catch
            {
                display.LastKnownInput = previous;
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task PrepareSamsungAsync(
        DisplayDevice device,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (device.Method != ControlMethod.SamsungTizen)
        {
            throw new InvalidOperationException("Remote calibration is only used for Samsung network displays.");
        }

        var temporaryDisplay = CreateConfiguredDisplay(device, device.Inputs[0], device.Inputs[1]);
        await _samsungRemote.ConnectAsync(temporaryDisplay, progress, cancellationToken);
    }

    public Task SendSamsungKeyAsync(string key, CancellationToken cancellationToken = default) =>
        _samsungRemote.SendKeyAsync(key, cancellationToken);

    public Task SetRunAtStartupAsync(bool enabled, CancellationToken cancellationToken = default)
        => UpdateSettingsAsync(
            () => _settings.RunAtStartup,
            value => _settings.RunAtStartup = value,
            enabled,
            cancellationToken);

    public Task SetHeadlessModeAsync(bool enabled, CancellationToken cancellationToken = default)
        => UpdateSettingsAsync(
            () => _settings.KeepRunning,
            value => _settings.KeepRunning = value,
            enabled,
            cancellationToken);

    public async Task SetHotkeysAsync(HotkeySettings hotkeys, CancellationToken cancellationToken = default)
    {
        if (!hotkeys.IsValid)
        {
            throw new ArgumentException("Choose two different keyboard shortcuts.", nameof(hotkeys));
        }

        await UpdateSettingsAsync(
            () => _settings.Hotkeys,
            value => _settings.Hotkeys = value,
            hotkeys,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            await _samsungRemote.DisposeAsync();
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private async Task UpdateSettingsAsync<T>(
        Func<T> read,
        Action<T> write,
        T value,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var previous = read();
            write(value);
            try
            {
                await _settingsStore.SaveAsync(_settings, cancellationToken);
            }
            catch
            {
                write(previous);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static ConfiguredDisplay CreateConfiguredDisplay(
        DisplayDevice device,
        InputSource hdmi,
        InputSource displayPort) =>
        new()
        {
            Id = device.Id,
            Name = device.Name,
            Method = device.Method,
            Address = device.Address,
            Port = device.Port,
            Model = device.Model,
            HardwareId = device.HardwareId,
            Hdmi = hdmi,
            DisplayPort = displayPort,
            MenuDelayMilliseconds = device.SuggestedMenuDelayMilliseconds
        };
}
