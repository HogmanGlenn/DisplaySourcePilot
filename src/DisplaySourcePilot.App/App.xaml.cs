using System.Collections.Concurrent;
using DisplaySourcePilot.App.AppLifecycle;
using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Control;
using DisplaySourcePilot.Core.Discovery;
using DisplaySourcePilot.Core.Models;
using Microsoft.UI.Xaml;

namespace DisplaySourcePilot.App;

public partial class App : Application
{
    private readonly InstanceBroker _instanceBroker = new();
    private readonly ConcurrentQueue<AppCommand> _pendingCommands = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private DisplaySourcePilotService? _switchService;
    private MainWindow? _window;
    private GlobalHotkeyService? _hotkeys;
    private HotkeySettings? _activeHotkeys;
    private bool _shuttingDown;
    private bool _servicesInitialized;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, eventArgs) => AppLog.Write(eventArgs.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Write("Application launched.");
        if (Environment.GetCommandLineArgs().Skip(1).Any(argument =>
                string.Equals(argument, "--cleanup-shortcuts", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                DesktopShortcutService.RemoveTracked();
            }
            catch (Exception exception)
            {
                AppLog.Write(exception);
            }

            Exit();
            return;
        }

        var command = ParseCommand(Environment.GetCommandLineArgs().Skip(1));
        if (!_instanceBroker.TryBecomePrimary())
        {
            try
            {
                await InstanceBroker.SendAsync(command.Serialize(), allowForegroundActivation: command.Action == "show");
            }
            finally
            {
                Exit();
            }

            return;
        }

        _switchService = new DisplaySourcePilotService();
        AppLog.Write("Creating the main window.");
        _window = new MainWindow(_switchService, new DisplayDiscoveryService());
        _window.QuitRequested += async () => await ShutdownAsync();
        _window.MainPage.HotkeysChangeRequested = ApplyHotkeys;
        _window.MainPage.HeadlessModeChanged += enabled => _window.HeadlessModeEnabled = enabled;
        _window.MainPage.AttentionRequested += HandleAttentionRequested;
        _instanceBroker.CommandReceived += QueueForwardedCommand;
        _instanceBroker.FlushPendingCommands();

        if (command.Action == "show")
        {
            AppLog.Write("Showing the main window.");
            _window.Show();
        }

        AppLog.Write("Initializing display services.");
        await _window.MainPage.InitializeAsync();
        try
        {
            StartupRegistration.SetEnabled(_switchService.Settings.RunAtStartup);
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            _window.MainPage.SetStartupWarning(exception.Message);
        }

        var hotkeyError = ApplyHotkeys(_switchService.Settings.Hotkeys);
        if (hotkeyError is not null)
        {
            _window.MainPage.SetShortcutWarning(hotkeyError);
        }
        AppLog.Write("Display services initialized.");
        _servicesInitialized = true;
        _window.HeadlessModeEnabled = _switchService.Settings.KeepRunning;
        if (command.Action == "background" && !_switchService.Settings.KeepRunning)
        {
            _window.Show();
        }
        else if (command.Action is not ("show" or "background"))
        {
            _pendingCommands.Enqueue(command);
            await ProcessPendingCommandsAsync();
            if (!_switchService.Settings.KeepRunning && !RequiresInputConfirmation())
            {
                await ShutdownAsync();
            }
        }

        await ProcessPendingCommandsAsync();
    }

    private void HandleAttentionRequested() => _window?.Show();

    private void QueueForwardedCommand(string value)
    {
        var command = AppCommand.Deserialize(value);
        if (command.Action == "show")
        {
            _window?.DispatcherQueue.TryEnqueue(() => _window.Show());
            return;
        }

        _pendingCommands.Enqueue(command);
        _window?.DispatcherQueue.TryEnqueue(async () => await ProcessPendingCommandsAsync());
    }

    private async Task ProcessPendingCommandsAsync()
    {
        if (!_servicesInitialized)
        {
            return;
        }

        await _commandGate.WaitAsync();
        try
        {
            while (_pendingCommands.TryDequeue(out var command) && !_shuttingDown)
            {
                await HandleCommandAsync(command);
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private string? ApplyHotkeys(HotkeySettings settings)
    {
        var previous = _activeHotkeys;
        _hotkeys?.Dispose();
        _hotkeys = null;

        try
        {
            StartHotkeys(settings);
            _activeHotkeys = settings;
            _window!.MainPage.SetShortcutLabels(settings);
            AppLog.Write($"Keyboard shortcuts registered: {settings.Hdmi.DisplayText}, {settings.DisplayPort.DisplayText}.");
            return null;
        }
        catch (InvalidOperationException exception)
        {
            AppLog.Write(exception);
            if (previous is not null)
            {
                try
                {
                    StartHotkeys(previous);
                    _activeHotkeys = previous;
                    _window!.MainPage.SetShortcutLabels(previous);
                }
                catch (InvalidOperationException restoreException)
                {
                    AppLog.Write(restoreException);
                    _activeHotkeys = null;
                }
            }

            return exception.Message;
        }
    }

    private void StartHotkeys(HotkeySettings settings)
    {
        var service = new GlobalHotkeyService(_window!.DispatcherQueue, settings);
        service.HdmiPressed += async () => await _window.MainPage.SwitchAsync(InputKind.Hdmi);
        service.DisplayPortPressed += async () => await _window.MainPage.SwitchAsync(InputKind.DisplayPort);
        try
        {
            service.Start();
            _hotkeys = service;
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    private async Task HandleCommandAsync(AppCommand command)
    {
        if (_window is null)
        {
            return;
        }

        switch (command.Action)
        {
            case "hdmi":
                await _window.MainPage.SwitchAsync(InputKind.Hdmi, command.DisplayToken);
                break;
            case "displayport":
                await _window.MainPage.SwitchAsync(InputKind.DisplayPort, command.DisplayToken);
                break;
            case "confirm-hdmi":
                await _window.MainPage.ConfirmCurrentInputAsync(InputKind.Hdmi);
                break;
            case "confirm-displayport":
                await _window.MainPage.ConfirmCurrentInputAsync(InputKind.DisplayPort);
                break;
            case "quit":
                await ShutdownAsync();
                break;
            case "background":
                break;
            default:
                _window.Show();
                break;
        }
    }

    private bool RequiresInputConfirmation() =>
        _switchService?.Settings.Display is
        {
            Method: ControlMethod.SamsungTizen,
            LastKnownInput: null
        };

    private async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _hotkeys?.Dispose();
        await _instanceBroker.DisposeAsync();
        if (_switchService is not null)
        {
            await _switchService.DisposeAsync();
        }

        _window?.CloseForQuit();
        Exit();
    }

    private static AppCommand ParseCommand(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        string? displayToken = null;
        var invalidDisplayToken = false;
        for (var index = 0; index < values.Length; index++)
        {
            if (string.Equals(values[index], "--display", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = index + 1 < values.Length ? values[index + 1].Trim() : string.Empty;
                if (candidate.Length == 64 && candidate.All(Uri.IsHexDigit))
                {
                    displayToken = candidate.ToLowerInvariant();
                }
                else
                {
                    invalidDisplayToken = true;
                }
            }
        }

        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index].Trim().ToLowerInvariant();
            if (value == "--background")
            {
                return new AppCommand("background");
            }

            if (value == "--quit")
            {
                return new AppCommand("quit");
            }

            if (value == "--switch" && index + 1 < values.Length)
            {
                var action = values[index + 1].Trim().ToLowerInvariant() switch
                {
                    "hdmi" => "hdmi",
                    "displayport" or "dp" => "displayport",
                    _ => "show"
                };
                return invalidDisplayToken
                    ? new AppCommand("show")
                    : new AppCommand(action, action == "show" ? null : displayToken);
            }

            if (value == "--confirm" && index + 1 < values.Length)
            {
                return new AppCommand(values[index + 1].Trim().ToLowerInvariant() switch
                {
                    "hdmi" => "confirm-hdmi",
                    "displayport" or "dp" => "confirm-displayport",
                    _ => "show"
                });
            }
        }

        return new AppCommand("show");
    }

    private sealed record AppCommand(string Action, string? DisplayToken = null)
    {
        public string Serialize() => DisplayToken is null ? Action : $"{Action}|{DisplayToken}";

        public static AppCommand Deserialize(string value)
        {
            var parts = value.Split('|', 2);
            var action = parts[0] switch
            {
                "hdmi" or "displayport" or "confirm-hdmi" or "confirm-displayport" or
                "quit" or "background" or "show" => parts[0],
                _ => "show"
            };
            var token = parts.Length == 2 &&
                parts[1].Length == 64 &&
                parts[1].All(Uri.IsHexDigit)
                    ? parts[1].ToLowerInvariant()
                    : null;
            return new AppCommand(action, token);
        }
    }
}
