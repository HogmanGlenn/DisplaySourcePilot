using System.Diagnostics;
using System.Net;
using DisplaySourcePilot.App.AppLifecycle;
using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Control;
using DisplaySourcePilot.Core.Discovery;
using DisplaySourcePilot.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DisplaySourcePilot.App;

public sealed partial class MainPage : Page
{
    private static readonly ModifierOption[] ModifierOptions =
    [
        new("Ctrl+Alt", HotkeyModifiers.ControlAlt),
        new("Ctrl+Shift", HotkeyModifiers.ControlShift),
        new("Alt+Shift", HotkeyModifiers.AltShift),
        new("Ctrl+Alt+Shift", HotkeyModifiers.ControlAltShift)
    ];

    private readonly List<string> _calibrationKeys = [];
    private readonly List<DisplayDevice> _discoveredDevices = [];
    private DisplaySourcePilotService? _switchService;
    private DisplayDiscoveryService? _discoveryService;
    private DisplayDevice? _selectedDevice;
    private int _discoveredDeviceCount;
    private bool _calibrationCommandInProgress;
    private bool _initialized;
    private bool _sizeRequestQueued;
    private bool _settingHeadlessToggle;
    private bool _settingStartupToggle;
    private bool _shortcutWarning;
    private InputKind? _pendingSwitchInput;
    private SourceMenuState _sourceMenuState;

    public MainPage()
    {
        InitializeComponent();
        var keys = Enumerable.Range('A', 26).Select(value => ((char)value).ToString()).ToArray();
        HdmiModifierComboBox.ItemsSource = ModifierOptions;
        DisplayPortModifierComboBox.ItemsSource = ModifierOptions;
        HdmiKeyComboBox.ItemsSource = keys;
        DisplayPortKeyComboBox.ItemsSource = keys;
    }

    public event Action? QuitRequested;

    public event Action? AttentionRequested;

    public event Action<bool>? HeadlessModeChanged;

    public event Action<int, int>? PreferredSizeChanged;

    public Func<HotkeySettings, string?>? HotkeysChangeRequested { get; set; }

    public void RequestCurrentSize()
    {
        if (_sizeRequestQueued)
        {
            return;
        }

        _sizeRequestQueued = true;
        if (!DispatcherQueue.TryEnqueue(MeasureCurrentContent))
        {
            _sizeRequestQueued = false;
        }
    }

    private void MeasureCurrentContent()
    {
        _sizeRequestQueued = false;
        var panel = ReadyPanel.Visibility == Visibility.Visible
            ? ReadyPanel
            : CalibrationPanel.Visibility == Visibility.Visible
                ? CalibrationPanel
                : SetupPanel;
        var width = panel == ReadyPanel ? 520 : 560;
        var padding = ContentRoot.Padding;
        var contentWidth = Math.Max(320, width - padding.Left - padding.Right);

        panel.Measure(new Size(contentWidth, double.PositiveInfinity));
        HelpBar.Measure(new Size(contentWidth, double.PositiveInfinity));
        StatusBar.Measure(new Size(contentWidth, double.PositiveInfinity));
        var height = padding.Top +
            HelpBar.DesiredSize.Height +
            panel.DesiredSize.Height +
            StatusBar.Margin.Top +
            StatusBar.DesiredSize.Height +
            StatusBar.Margin.Bottom +
            padding.Bottom;
        PreferredSizeChanged?.Invoke(width, (int)Math.Ceiling(height));
    }

    public void Attach(DisplaySourcePilotService switchService, DisplayDiscoveryService discoveryService)
    {
        _switchService = switchService;
        _discoveryService = discoveryService;
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var progress = new ImmediateProgress<string>(message => SetStatus(message));
        try
        {
            await SwitchService.InitializeAsync(progress);
            SetShortcutLabels(SwitchService.Settings.Hotkeys);
            if (SwitchService.IsReady)
            {
                ShowReadyState();
            }
            else
            {
                await DiscoverAsync();
            }
        }
        catch (Exception exception)
        {
            SetError(exception);
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    public void SetShortcutWarning(string message)
    {
        _shortcutWarning = true;
        HdmiShortcutText.Text = "Shortcut unavailable";
        DisplayPortShortcutText.Text = "Shortcut unavailable";
        SetStatus(message, severity: InfoBarSeverity.Warning);
    }

    public void SetStartupWarning(string message) =>
        SetStatus(message, severity: InfoBarSeverity.Warning);

    public void SetShortcutLabels(HotkeySettings settings)
    {
        _shortcutWarning = false;
        HdmiShortcutText.Text = settings.Hdmi.DisplayText;
        DisplayPortShortcutText.Text = settings.DisplayPort.DisplayText;
    }

    public async Task SwitchAsync(InputKind input, string? expectedDisplayToken = null)
    {
        if (!SwitchService.IsReady)
        {
            SetStatus("Set up a display first.", true);
            AttentionRequested?.Invoke();
            return;
        }

        var configuredDisplay = SwitchService.Settings.Display!;
        var sourceLabel = input == InputKind.Hdmi
            ? configuredDisplay.Hdmi.Label
            : configuredDisplay.DisplayPort.Label;
        if (expectedDisplayToken is not null &&
            !string.Equals(expectedDisplayToken, configuredDisplay.ShortcutToken, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("This shortcut belongs to a different monitor. Create a new shortcut from the options menu.", true);
            AttentionRequested?.Invoke();
            return;
        }

        if (ReadyPanel.Visibility != Visibility.Visible)
        {
            SetStatus("Finish display setup before switching inputs.", severity: InfoBarSeverity.Warning);
            return;
        }

        if (configuredDisplay.Method == ControlMethod.SamsungTizen &&
            configuredDisplay.LastKnownInput == input)
        {
            _pendingSwitchInput = input;
            try
            {
                await SwitchService.RequireCurrentInputConfirmationAsync();
                ShowInputConfirmation(input);
                SetStatus("Confirm the input currently visible on the monitor.", severity: InfoBarSeverity.Warning);
                AttentionRequested?.Invoke();
            }
            catch (Exception exception)
            {
                _pendingSwitchInput = null;
                SetError(exception);
            }

            return;
        }

        SetSwitchButtonsEnabled(false);
        SetStatus($"Switching to {sourceLabel}...");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await SwitchService.SwitchAsync(input);
            stopwatch.Stop();
            SetActiveInput(input);
            var result = configuredDisplay.Method == ControlMethod.SamsungTizen
                ? $"{sourceLabel} switch sent."
                : $"{sourceLabel} selected.";
            AppLog.Write($"{result} Completed in {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
            SetStatus(result, severity: InfoBarSeverity.Success);
        }
        catch (CurrentInputUnknownException exception)
        {
            AppLog.Write(exception.Message);
            _pendingSwitchInput = input;
            ShowInputConfirmation(input);
            SetStatus(exception.Message, severity: InfoBarSeverity.Warning);
            AttentionRequested?.Invoke();
        }
        catch (Exception exception)
        {
            if (SwitchService.Settings.Display is { Method: ControlMethod.SamsungTizen, LastKnownInput: null })
            {
                _pendingSwitchInput = input;
                ShowInputConfirmation(input);
            }

            SetError(exception);
        }
        finally
        {
            SetSwitchButtonsEnabled(true);
        }
    }

    private DisplaySourcePilotService SwitchService =>
        _switchService ?? throw new InvalidOperationException("DisplaySourcePilot is not initialized.");

    private DisplayDiscoveryService DiscoveryService =>
        _discoveryService ?? throw new InvalidOperationException("Display discovery is not initialized.");

    private async Task DiscoverAsync()
    {
        ShowPanel(SetupPanel);
        _selectedDevice = null;
        _discoveredDeviceCount = 0;
        _discoveredDevices.Clear();
        DeviceListView.ItemsSource = null;
        DeviceListView.Visibility = Visibility.Collapsed;
        NoDisplaysPanel.Visibility = Visibility.Collapsed;
        InputSelectionPanel.Visibility = Visibility.Collapsed;
        CurrentInputSelectionPanel.Visibility = Visibility.Collapsed;
        UseDisplayButton.IsEnabled = false;
        RetryButton.Visibility = Visibility.Collapsed;
        SetupBackButton.Visibility = SwitchService.IsReady ? Visibility.Visible : Visibility.Collapsed;
        DiscoveryProgress.IsActive = true;

        var progress = new Progress<DiscoveryStatus>(status => SetStatus(status.Message));
        try
        {
            var knownAddress = SwitchService.Settings.Display?.Address;
            var devices = await DiscoveryService.DiscoverAsync(
                progress,
                string.IsNullOrWhiteSpace(knownAddress) ? null : [knownAddress]);
            _discoveredDevices.AddRange(devices);
            RefreshDeviceList();
            if (devices.Count == 1)
            {
                DeviceListView.SelectedIndex = 0;
            }

            RetryButton.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RequestCurrentSize();
        }
        catch (Exception exception)
        {
            SetError(exception);
            RetryButton.Visibility = Visibility.Visible;
            RequestCurrentSize();
        }
        finally
        {
            DiscoveryProgress.IsActive = false;
        }
    }

    private void ShowReadyState()
    {
        var display = SwitchService.Settings.Display!;
        ReadyDisplayName.Text = WebUtility.HtmlDecode(display.Name);
        ReadyDisplayDetail.Text = display.Method == ControlMethod.Ddc
            ? "DDC/CI connection"
            : $"{display.Model} - {display.MenuDelayMilliseconds} ms timing";
        HdmiButtonLabel.Text = display.Hdmi.Label;
        DisplayPortButtonLabel.Text = display.DisplayPort.Label;
        SetShortcutLabels(SwitchService.Settings.Hotkeys);
        _settingStartupToggle = true;
        StartupToggle.IsOn = SwitchService.Settings.RunAtStartup;
        _settingStartupToggle = false;
        _settingHeadlessToggle = true;
        HeadlessToggle.IsOn = SwitchService.Settings.KeepRunning;
        _settingHeadlessToggle = false;
        var inputUnknown = display.Method == ControlMethod.SamsungTizen && display.LastKnownInput is null;
        _pendingSwitchInput = null;
        ConfirmInputMenuItem.Visibility = display.Method == ControlMethod.SamsungTizen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepairSamsungMenuItem.Visibility = display.Method == ControlMethod.SamsungTizen
            ? Visibility.Visible
            : Visibility.Collapsed;
        InputStatePanel.Visibility = inputUnknown ? Visibility.Visible : Visibility.Collapsed;
        SetActiveInput(display.LastKnownInput);
        ShowPanel(ReadyPanel);

        if (inputUnknown)
        {
            SetStatus("Confirm the active input before switching.", severity: InfoBarSeverity.Warning);
        }
        else
        {
            SetStatus(
                _shortcutWarning ? "Ready. Keyboard shortcuts are unavailable." : "Ready.",
                false,
                _shortcutWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedDevice = DeviceListView.SelectedItem as DisplayDevice;
        if (_selectedDevice is null)
        {
            InputSelectionPanel.Visibility = Visibility.Collapsed;
            CurrentInputSelectionPanel.Visibility = Visibility.Collapsed;
            UseDisplayButton.IsEnabled = false;
            RequestCurrentSize();
            return;
        }

        var hdmiInputs = _selectedDevice.Inputs.Where(input => input.Kind == InputKind.Hdmi).ToArray();
        var displayPortInputs = _selectedDevice.Inputs.Where(input => input.Kind == InputKind.DisplayPort).ToArray();
        HdmiComboBox.ItemsSource = hdmiInputs;
        DisplayPortComboBox.ItemsSource = displayPortInputs;
        HdmiComboBox.SelectedIndex = hdmiInputs.Length > 0 ? 0 : -1;
        DisplayPortComboBox.SelectedIndex = displayPortInputs.Length > 0 ? 0 : -1;
        InputSelectionPanel.Visibility = hdmiInputs.Length > 1 || displayPortInputs.Length > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        CurrentInputSelectionPanel.Visibility = _selectedDevice.Method == ControlMethod.SamsungTizen &&
            !_selectedDevice.RequiresCalibration
                ? Visibility.Visible
                : Visibility.Collapsed;
        UseDisplayButton.IsEnabled = hdmiInputs.Length > 0 && displayPortInputs.Length > 0;
        UseDisplayButton.Content = _selectedDevice.RequiresCalibration
            ? "Start source check"
            : "Use this display";
        RequestCurrentSize();
    }

    private void DeviceList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem item && args.Item is DisplayDevice device)
        {
            AutomationProperties.SetName(item, device.Name);
        }
    }

    private async void AddSamsungAddress_Click(object sender, RoutedEventArgs e)
    {
        var address = SamsungAddressTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            SetStatus("Enter the monitor's IP address.", true);
            return;
        }

        AddSamsungAddressButton.IsEnabled = false;
        SamsungAddressTextBox.IsEnabled = false;
        DiscoveryProgress.IsActive = true;
        SetStatus("Checking the Samsung monitor...");
        try
        {
            var discovered = await DiscoveryService.DiscoverSamsungAtAddressAsync(address);
            var device = _discoveredDevices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, discovered.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Address, discovered.Address, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                device = discovered;
                _discoveredDevices.Add(device);
            }

            RefreshDeviceList(device);
            SamsungAddressExpander.IsExpanded = false;
            RetryButton.Visibility = Visibility.Collapsed;
            SetStatus("Samsung monitor found.", severity: InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
        finally
        {
            DiscoveryProgress.IsActive = false;
            SamsungAddressTextBox.IsEnabled = true;
            AddSamsungAddressButton.IsEnabled = true;
        }
    }

    private async void UseDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null ||
            HdmiComboBox.SelectedItem is not InputSource hdmi ||
            DisplayPortComboBox.SelectedItem is not InputSource displayPort)
        {
            return;
        }

        SetSetupEnabled(false);
        var progress = new ImmediateProgress<string>(message => SetStatus(message));
        try
        {
            if (_selectedDevice.RequiresCalibration)
            {
                await SwitchService.PrepareSamsungAsync(_selectedDevice, progress);
                PrepareCalibration(InputKind.DisplayPort);
                ShowPanel(CalibrationPanel);
                SetStatus("Ready for the source check.");
                return;
            }

            InputKind? currentInput = _selectedDevice.Method == ControlMethod.SamsungTizen
                ? SetupCurrentHdmiRadio.IsChecked == true ? InputKind.Hdmi : InputKind.DisplayPort
                : null;
            await SwitchService.ConfigureAsync(
                _selectedDevice,
                hdmi,
                displayPort,
                progress,
                currentInput: currentInput);
            ShowReadyState();
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
        finally
        {
            SetSetupEnabled(true);
        }
    }

    private async void OpenSourceMenu_Click(object sender, RoutedEventArgs e)
        => await OpenSourceMenuAsync();

    private async Task OpenSourceMenuAsync()
    {
        if (_selectedDevice is null || SelectedCalibrationInput() is not { } currentInput)
        {
            SetStatus("Choose the input currently showing this window.", true);
            return;
        }

        var target = _selectedDevice.Inputs.First(input => input.Kind != currentInput);
        if (_sourceMenuState == SourceMenuState.Unknown)
        {
            _sourceMenuState = SourceMenuState.Closed;
        }

        OpenSourceMenuButton.IsEnabled = false;
        CurrentDisplayPortRadio.IsEnabled = false;
        CurrentHdmiRadio.IsEnabled = false;
        CalibrationGuidanceBar.IsOpen = false;
        try
        {
            _calibrationKeys.Clear();
            TargetHighlightedCheckBox.IsChecked = false;
            TargetHighlightedCheckBox.Content = $"{target.Label} is highlighted";
            FinishCalibrationButton.IsEnabled = false;
            await SwitchService.SendSamsungKeyAsync("KEY_SOURCE");
            _sourceMenuState = SourceMenuState.Open;
            await Task.Delay(Math.Clamp(
                _selectedDevice?.SuggestedMenuDelayMilliseconds ?? 700,
                700,
                1200));
            CalibrationMoveCount.Text = "No moves yet";
            CalibrationControls.Visibility = Visibility.Visible;
            RequestCurrentSize();
            SetStatus($"Move the highlight to {target.Label}. Do not select another source.");
        }
        catch (Exception exception)
        {
            _sourceMenuState = SourceMenuState.Unknown;
            OpenSourceMenuButton.IsEnabled = true;
            CurrentDisplayPortRadio.IsEnabled = true;
            CurrentHdmiRadio.IsEnabled = true;
            InvalidateCalibration(exception);
        }
    }

    private async Task AddCalibrationKeyAsync(string key)
    {
        if (_calibrationCommandInProgress || _sourceMenuState != SourceMenuState.Open)
        {
            return;
        }

        if (_calibrationKeys.Count >= 8)
        {
            SetStatus("The source route is too long. Undo a move or restart the source check.", true);
            return;
        }

        try
        {
            _calibrationCommandInProgress = true;
            CalibrationControls.IsHitTestVisible = false;
            await SwitchService.SendSamsungKeyAsync(key);
            await Task.Delay(300);
            _calibrationKeys.Add(key);
            TargetHighlightedCheckBox.IsChecked = false;
            UpdateCalibrationMoveCount();
        }
        catch (Exception exception)
        {
            InvalidateCalibration(exception);
        }
        finally
        {
            CalibrationControls.IsHitTestVisible = true;
            _calibrationCommandInProgress = false;
        }
    }

    private async void CalibrationUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationCommandInProgress || _sourceMenuState != SourceMenuState.Open || _calibrationKeys.Count == 0)
        {
            return;
        }

        var lastKey = _calibrationKeys[^1];
        var reverseKey = new RemoteRoute([lastKey]).Reverse().Keys[0];
        try
        {
            _calibrationCommandInProgress = true;
            CalibrationControls.IsHitTestVisible = false;
            await SwitchService.SendSamsungKeyAsync(reverseKey);
            await Task.Delay(300);
            _calibrationKeys.RemoveAt(_calibrationKeys.Count - 1);
            TargetHighlightedCheckBox.IsChecked = false;
            UpdateCalibrationMoveCount();
        }
        catch (Exception exception)
        {
            InvalidateCalibration(exception);
        }
        finally
        {
            CalibrationControls.IsHitTestVisible = true;
            _calibrationCommandInProgress = false;
        }
    }

    private async void FinishCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationCommandInProgress ||
            _selectedDevice is null ||
            _calibrationKeys.Count == 0 ||
            TargetHighlightedCheckBox.IsChecked != true)
        {
            SetStatus("Confirm that the exact target input is highlighted before saving.", true);
            return;
        }

        var currentInput = CurrentDisplayPortRadio.IsChecked == true
            ? InputKind.DisplayPort
            : CurrentHdmiRadio.IsChecked == true
                ? InputKind.Hdmi
                : (InputKind?)null;
        if (currentInput is null)
        {
            SetStatus("Choose the input currently showing this window.", true);
            return;
        }

        var hdmi = _selectedDevice.Inputs.First(input => input.Kind == InputKind.Hdmi);
        var displayPort = _selectedDevice.Inputs.First(input => input.Kind == InputKind.DisplayPort);
        var route = new RemoteRoute(_calibrationKeys.ToArray());
        if (!route.IsSafeNavigation)
        {
            SetStatus("The route contains a reversed or unsupported move. Undo the last move or restart the check.", true);
            return;
        }
        if (currentInput == InputKind.DisplayPort)
        {
            hdmi = hdmi with { Route = route };
            displayPort = displayPort with { Route = route.Reverse() };
        }
        else
        {
            displayPort = displayPort with { Route = route };
            hdmi = hdmi with { Route = route.Reverse() };
        }

        SetStatus("Saving setup...");
        _calibrationCommandInProgress = true;
        CalibrationPanel.IsHitTestVisible = false;
        try
        {
            if (_sourceMenuState == SourceMenuState.Open)
            {
                await SwitchService.SendSamsungKeyAsync("KEY_RETURN");
                await Task.Delay(300);
                _sourceMenuState = SourceMenuState.Closed;
            }

            await SwitchService.ConfigureAsync(
                _selectedDevice,
                hdmi,
                displayPort,
                currentInput: currentInput.Value);
            ShowReadyState();
        }
        catch (Exception exception)
        {
            InvalidateCalibration(exception);
        }
        finally
        {
            CalibrationPanel.IsHitTestVisible = true;
            _calibrationCommandInProgress = false;
        }
    }

    private void TargetHighlighted_Changed(object sender, RoutedEventArgs e)
    {
        if (TargetHighlightedCheckBox.IsChecked == true && _calibrationKeys.Count == 0)
        {
            TargetHighlightedCheckBox.IsChecked = false;
            SetStatus("Move to the target input before confirming it.", true);
            return;
        }

        FinishCalibrationButton.IsEnabled =
            TargetHighlightedCheckBox.IsChecked == true &&
            _calibrationKeys.Count > 0;
    }

    private async void MissingSource_Click(object sender, RoutedEventArgs e)
    {
        var menuMayStillBeOpen = false;
        try
        {
            await CloseSourceMenuAsync();
        }
        catch (Exception exception)
        {
            menuMayStillBeOpen = true;
            AppLog.Write(exception);
        }

        ResetCalibrationControls();
        CalibrationGuidanceBar.Message =
            "Samsung can hide an input when its device is asleep or not sending a signal. Wake the device, check its cable, then restart the source check. DisplaySourcePilot will not guess another source." +
            (menuMayStillBeOpen ? " The source menu may still be open." : string.Empty);
        CalibrationGuidanceBar.IsOpen = true;
        SetStatus("The target input must be visible before its route can be saved.", severity: InfoBarSeverity.Warning);
        RequestCurrentSize();
    }

    private async void RestartCalibration_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sourceMenuState == SourceMenuState.Open)
            {
                await CloseSourceMenuAsync();
            }
            else if (_sourceMenuState == SourceMenuState.Unknown)
            {
                _sourceMenuState = SourceMenuState.Closed;
            }

            ResetCalibrationControls();
            await OpenSourceMenuAsync();
        }
        catch (Exception exception)
        {
            InvalidateCalibration(exception);
        }
    }

    private void PrepareCalibration(InputKind currentInput)
    {
        _calibrationKeys.Clear();
        _sourceMenuState = SourceMenuState.Closed;
        CurrentDisplayPortRadio.IsChecked = currentInput == InputKind.DisplayPort;
        CurrentHdmiRadio.IsChecked = currentInput == InputKind.Hdmi;
        CalibrationInstructionText.Text =
            "Choose the input showing this window. Open the source menu, then move to the exact target input.";
        CalibrationGuidanceBar.IsOpen = false;
        ResetCalibrationControls();
    }

    private void ResetCalibrationControls()
    {
        _calibrationKeys.Clear();
        CalibrationMoveCount.Text = "No moves yet";
        CalibrationControls.Visibility = Visibility.Collapsed;
        TargetHighlightedCheckBox.IsChecked = false;
        FinishCalibrationButton.IsEnabled = false;
        OpenSourceMenuButton.IsEnabled = true;
        OpenSourceMenuButton.Content = "Open source menu";
        CurrentDisplayPortRadio.IsEnabled = true;
        CurrentHdmiRadio.IsEnabled = true;
        RequestCurrentSize();
    }

    private InputKind? SelectedCalibrationInput() =>
        CurrentDisplayPortRadio.IsChecked == true
            ? InputKind.DisplayPort
            : CurrentHdmiRadio.IsChecked == true
                ? InputKind.Hdmi
                : null;

    private async Task CloseSourceMenuAsync()
    {
        if (_sourceMenuState == SourceMenuState.Closed)
        {
            return;
        }

        if (_sourceMenuState == SourceMenuState.Unknown)
        {
            throw new InvalidOperationException("The source menu state is unknown. Close it with the monitor remote before restarting the source check.");
        }

        try
        {
            await SwitchService.SendSamsungKeyAsync("KEY_RETURN");
            _sourceMenuState = SourceMenuState.Closed;
        }
        catch
        {
            _sourceMenuState = SourceMenuState.Unknown;
            throw;
        }
    }

    private async void CancelCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationCommandInProgress)
        {
            return;
        }

        Exception? closeFailure = null;
        _calibrationCommandInProgress = true;
        CalibrationPanel.IsHitTestVisible = false;
        if (_sourceMenuState == SourceMenuState.Open)
        {
            try
            {
                await SwitchService.SendSamsungKeyAsync("KEY_RETURN");
                _sourceMenuState = SourceMenuState.Closed;
            }
            catch (Exception exception)
            {
                _sourceMenuState = SourceMenuState.Unknown;
                closeFailure = exception;
                AppLog.Write(exception);
            }
        }

        CalibrationPanel.IsHitTestVisible = true;
        _calibrationCommandInProgress = false;
        CurrentDisplayPortRadio.IsEnabled = true;
        CurrentHdmiRadio.IsEnabled = true;
        ShowPanel(SetupPanel);
        SetStatus(
            closeFailure is null ? "Choose a display." : "Choose a display. The monitor source menu may still be open.",
            severity: closeFailure is null ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
    }

    private async void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingStartupToggle)
        {
            return;
        }

        var enabled = StartupToggle.IsOn;
        var previous = SwitchService.Settings.RunAtStartup;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            await SwitchService.SetRunAtStartupAsync(enabled);
            SetStatus(enabled ? "Starts with Windows." : "Windows startup disabled.");
        }
        catch (Exception exception)
        {
            _settingStartupToggle = true;
            StartupToggle.IsOn = previous;
            _settingStartupToggle = false;
            try
            {
                StartupRegistration.SetEnabled(previous);
            }
            catch (Exception rollbackException)
            {
                AppLog.Write(rollbackException);
            }

            SetError(exception);
        }
    }

    private async void Headless_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingHeadlessToggle)
        {
            return;
        }

        var enabled = HeadlessToggle.IsOn;
        try
        {
            await SwitchService.SetHeadlessModeAsync(enabled);
            HeadlessModeChanged?.Invoke(enabled);
            SetStatus(
                enabled
                    ? "Headless mode enabled. Closing the window keeps controls active."
                    : "Headless mode disabled. Closing the window quits the app.",
                severity: InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            _settingHeadlessToggle = true;
            HeadlessToggle.IsOn = !enabled;
            _settingHeadlessToggle = false;
            SetError(exception);
        }
    }

    private async void Hotkeys_Click(object sender, RoutedEventArgs e)
    {
        var hotkeys = SwitchService.Settings.Hotkeys;
        SelectModifier(HdmiModifierComboBox, hotkeys.Hdmi.Modifiers);
        SelectModifier(DisplayPortModifierComboBox, hotkeys.DisplayPort.Modifiers);
        HdmiKeyComboBox.SelectedItem = hotkeys.Hdmi.Key.ToUpperInvariant();
        DisplayPortKeyComboBox.SelectedItem = hotkeys.DisplayPort.Key.ToUpperInvariant();
        HotkeyValidationBar.IsOpen = false;
        await HotkeyDialog.ShowAsync();
    }

    private async void HotkeyDialog_Save(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (HdmiModifierComboBox.SelectedItem is not ModifierOption hdmiModifiers ||
                DisplayPortModifierComboBox.SelectedItem is not ModifierOption displayPortModifiers ||
                HdmiKeyComboBox.SelectedItem is not string hdmiKey ||
                DisplayPortKeyComboBox.SelectedItem is not string displayPortKey)
            {
                ShowHotkeyError("Choose a modifier and key for both inputs.");
                args.Cancel = true;
                return;
            }

            var candidate = new HotkeySettings
            {
                Hdmi = new HotkeyBinding(hdmiModifiers.Value, hdmiKey),
                DisplayPort = new HotkeyBinding(displayPortModifiers.Value, displayPortKey)
            };
            if (!candidate.IsValid)
            {
                ShowHotkeyError("Choose two different keyboard shortcuts.");
                args.Cancel = true;
                return;
            }

            var previous = SwitchService.Settings.Hotkeys;
            var registrationError = HotkeysChangeRequested?.Invoke(candidate);
            if (registrationError is not null)
            {
                ShowHotkeyError(registrationError);
                args.Cancel = true;
                return;
            }

            try
            {
                await SwitchService.SetHotkeysAsync(candidate);
                SetShortcutLabels(candidate);
                SetStatus("Keyboard shortcuts updated.", severity: InfoBarSeverity.Success);
            }
            catch (Exception exception)
            {
                HotkeysChangeRequested?.Invoke(previous);
                AppLog.Write(exception);
                ShowHotkeyError(exception.Message);
                args.Cancel = true;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void SetupAgain_Click(object sender, RoutedEventArgs e)
    {
        await DiscoverAsync();
    }

    private async void RepairSamsung_Click(object sender, RoutedEventArgs e)
    {
        if (SwitchService.Settings.Display is not { Method: ControlMethod.SamsungTizen } display)
        {
            SetStatus("Samsung route repair is only available for a configured Samsung network monitor.", true);
            return;
        }

        var device = new DisplayDevice(
            display.Id,
            display.Name,
            $"{display.Model} - Local network",
            display.Method,
            false,
            [display.Hdmi, display.DisplayPort],
            display.Address,
            display.Port,
            display.Model,
            display.HardwareId,
            true,
            display.MenuDelayMilliseconds);
        var progress = new ImmediateProgress<string>(message => SetStatus(message));
        try
        {
            await SwitchService.PrepareSamsungAsync(device, progress);
            _selectedDevice = device;
            PrepareCalibration(display.LastKnownInput ?? InputKind.DisplayPort);
            ShowPanel(CalibrationPanel);
            SetStatus("Ready to repair the Samsung source route.");
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private void CreateHdmiShortcut_Click(object sender, RoutedEventArgs e) =>
        CreateDesktopShortcut(InputKind.Hdmi);

    private void CreateDisplayPortShortcut_Click(object sender, RoutedEventArgs e) =>
        CreateDesktopShortcut(InputKind.DisplayPort);

    private void CreateDesktopShortcut(InputKind input)
    {
        if (SwitchService.Settings.Display is not { } display)
        {
            SetStatus("Set up a monitor before creating a shortcut.", true);
            return;
        }

        try
        {
            var path = DesktopShortcutService.Create(display, input);
            SetStatus($"Desktop shortcut created: {Path.GetFileNameWithoutExtension(path)}", severity: InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private async void Help_Click(object sender, RoutedEventArgs e) => await HelpDialog.ShowAsync();

    private void HelpDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => HelpScrollViewer.ChangeView(null, 0, null, true));

    private void SetupBack_Click(object sender, RoutedEventArgs e) => ShowReadyState();

    private async void ConfirmInputState_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SwitchService.RequireCurrentInputConfirmationAsync();
            _pendingSwitchInput = null;
            ShowInputConfirmation();
            SetStatus("Confirm the active input before switching.", severity: InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private void SamsungAddressExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args) =>
        RequestCurrentSize();

    private void SamsungAddressExpander_Collapsed(Expander sender, ExpanderCollapsedEventArgs args) =>
        RequestCurrentSize();

    private async void Retry_Click(object sender, RoutedEventArgs e) => await DiscoverAsync();

    private async void Hdmi_Click(object sender, RoutedEventArgs e) => await SwitchAsync(InputKind.Hdmi);

    private async void DisplayPort_Click(object sender, RoutedEventArgs e) => await SwitchAsync(InputKind.DisplayPort);

    private async void ConfirmHdmi_Click(object sender, RoutedEventArgs e) =>
        await ConfirmCurrentInputAsync(InputKind.Hdmi);

    private async void ConfirmDisplayPort_Click(object sender, RoutedEventArgs e) =>
        await ConfirmCurrentInputAsync(InputKind.DisplayPort);

    private async void CalibrationUp_Click(object sender, RoutedEventArgs e) => await AddCalibrationKeyAsync("KEY_UP");

    private async void CalibrationDown_Click(object sender, RoutedEventArgs e) => await AddCalibrationKeyAsync("KEY_DOWN");

    private async void CalibrationLeft_Click(object sender, RoutedEventArgs e) => await AddCalibrationKeyAsync("KEY_LEFT");

    private async void CalibrationRight_Click(object sender, RoutedEventArgs e) => await AddCalibrationKeyAsync("KEY_RIGHT");

    private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    private void UpdateCalibrationMoveCount()
    {
        CalibrationMoveCount.Text = _calibrationKeys.Count == 0
            ? "No moves yet"
            : $"{_calibrationKeys.Count} move{(_calibrationKeys.Count == 1 ? string.Empty : "s")}";
    }

    private void ShowPanel(FrameworkElement panel)
    {
        SetupPanel.Visibility = panel == SetupPanel ? Visibility.Visible : Visibility.Collapsed;
        ReadyPanel.Visibility = panel == ReadyPanel ? Visibility.Visible : Visibility.Collapsed;
        CalibrationPanel.Visibility = panel == CalibrationPanel ? Visibility.Visible : Visibility.Collapsed;
        RequestCurrentSize();
    }

    private void SetSetupEnabled(bool enabled)
    {
        DeviceListView.IsEnabled = enabled;
        HdmiComboBox.IsEnabled = enabled;
        DisplayPortComboBox.IsEnabled = enabled;
        SetupCurrentHdmiRadio.IsEnabled = enabled;
        SetupCurrentDisplayPortRadio.IsEnabled = enabled;
        UseDisplayButton.IsEnabled = enabled &&
            _selectedDevice is not null &&
            HdmiComboBox.SelectedItem is InputSource &&
            DisplayPortComboBox.SelectedItem is InputSource;
    }

    private void RefreshDeviceList(DisplayDevice? selectedDevice = null)
    {
        _discoveredDeviceCount = _discoveredDevices.Count;
        DeviceListView.ItemsSource = null;
        DeviceListView.ItemsSource = _discoveredDevices.ToArray();
        DeviceListView.Height = Math.Min(_discoveredDeviceCount, 4) * 64 + 2;
        DeviceListView.Visibility = _discoveredDeviceCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoDisplaysPanel.Visibility = _discoveredDeviceCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectedDevice is not null)
        {
            DeviceListView.SelectedItem = selectedDevice;
        }

        RequestCurrentSize();
    }

    private void SetActiveInput(InputKind? input)
    {
        HdmiActiveLine.Opacity = input == InputKind.Hdmi ? 1 : 0.25;
        DisplayPortActiveLine.Opacity = input == InputKind.DisplayPort ? 1 : 0.25;
    }

    public async Task ConfirmCurrentInputAsync(InputKind input)
    {
        if (ReadyPanel.Visibility != Visibility.Visible)
        {
            SetStatus("Finish display setup before confirming an input.", severity: InfoBarSeverity.Warning);
            return;
        }

        ConfirmHdmiButton.IsEnabled = false;
        ConfirmDisplayPortButton.IsEnabled = false;
        try
        {
            await SwitchService.SetCurrentInputAsync(input);
            var pendingSwitch = _pendingSwitchInput;
            _pendingSwitchInput = null;
            InputStatePanel.Visibility = Visibility.Collapsed;
            SetActiveInput(input);
            RequestCurrentSize();

            if (pendingSwitch is { } target && target != input)
            {
                await SwitchAsync(target);
            }
            else
            {
                SetStatus(
                    $"{SourceLabel(input)} confirmed.",
                    severity: InfoBarSeverity.Success);
            }
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
        finally
        {
            ConfirmHdmiButton.IsEnabled = true;
            ConfirmDisplayPortButton.IsEnabled = true;
        }
    }

    private void ShowInputConfirmation(InputKind? requestedInput = null)
    {
        InputStateTitle.Text = requestedInput is null
            ? "Which input is active?"
            : $"Switch to {SourceLabel(requestedInput.Value)}";
        InputStateMessage.Text = requestedInput is null
            ? "Choose the input currently visible on the monitor."
            : "Choose the input currently visible. DisplaySourcePilot will continue the switch if needed.";
        InputStatePanel.Visibility = Visibility.Visible;
        SetActiveInput(null);
        RequestCurrentSize();
    }

    private string SourceLabel(InputKind input)
    {
        var display = SwitchService.Settings.Display;
        return input switch
        {
            InputKind.Hdmi => display?.Hdmi.Label ?? "HDMI",
            InputKind.DisplayPort => display?.DisplayPort.Label ?? "DisplayPort",
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
    }

    private void InvalidateCalibration(Exception exception)
    {
        AppLog.Write(exception);
        _sourceMenuState = SourceMenuState.Unknown;
        ResetCalibrationControls();
        OpenSourceMenuButton.Content = "Restart source check";
        CalibrationGuidanceBar.Message =
            "The monitor may have processed the last command. Close the source menu with the monitor remote, then restart the source check.";
        CalibrationGuidanceBar.IsOpen = true;
        SetStatus(exception.Message, true);
    }

    private void SetSwitchButtonsEnabled(bool enabled)
    {
        HdmiButton.IsEnabled = enabled;
        DisplayPortButton.IsEnabled = enabled;
    }

    private void SetStatus(
        string message,
        bool isError = false,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusBar.Message = message;
        StatusBar.Severity = isError ? InfoBarSeverity.Error : severity;
        StatusBar.IsOpen = true;
    }

    private void SetError(Exception exception)
    {
        AppLog.Write(exception);
        SetStatus(exception.Message, true);
        AttentionRequested?.Invoke();
    }

    private void ShowHotkeyError(string message)
    {
        HotkeyValidationBar.Message = message;
        HotkeyValidationBar.IsOpen = true;
    }

    private static void SelectModifier(ComboBox comboBox, HotkeyModifiers value)
    {
        comboBox.SelectedItem = ModifierOptions.First(option => option.Value == value);
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record ModifierOption(string Label, HotkeyModifiers Value)
    {
        public override string ToString() => Label;
    }

    private enum SourceMenuState
    {
        Closed,
        Open,
        Unknown
    }
}
