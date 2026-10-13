using System.Runtime.InteropServices;
using DisplaySourcePilot.App.AppLifecycle;
using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Control;
using DisplaySourcePilot.Core.Discovery;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DisplaySourcePilot.App;

public sealed partial class MainWindow : Window
{
    private const int InitialWidth = 520;
    private const int InitialHeight = 540;
    private const int MinimumWidth = 420;
    private const int MinimumHeight = 480;
    private const int MaximumAutomaticWidth = 720;
    private const int MaximumAutomaticHeight = 760;
    private bool _allowClose;
    private bool _pageLoaded;
    private bool _initializingSize = true;
    private bool _userResized;
    private SizeInt32? _pendingAutomaticSize;
    private readonly WindowPlacementStore _placementStore = new();
    private WindowPlacement? _normalPlacement;
    private bool _preservePosition;
    private bool _restoreMaximized;
    private bool _wasMaximized;
    private OverlappedPresenterState _lastPresenterState = OverlappedPresenterState.Restored;

    public MainWindow(DisplaySourcePilotService switchService, DisplayDiscoveryService discoveryService)
    {
        InitializeComponent();
        ContentPage.Attach(switchService, discoveryService);
        ContentPage.QuitRequested += () => QuitRequested?.Invoke();
        ContentPage.PreferredSizeChanged += ResizeForContent;
        ContentPage.Loaded += (_, _) =>
        {
            _pageLoaded = true;
            UpdateMinimumSize();
            ContentPage.RequestCurrentSize();
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        var initialScale = Math.Max(1, GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d);
        var initialWidth = (int)Math.Round(InitialWidth * initialScale);
        var initialHeight = (int)Math.Round(InitialHeight * initialScale);
        AppWindow.Resize(new SizeInt32(initialWidth, initialHeight));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
            UpdateMinimumSize();
        }

        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        AppWindow.Move(new PointInt32(
            workArea.X + ((workArea.Width - initialWidth) / 2),
            workArea.Y + ((workArea.Height - initialHeight) / 2)));
        RestoreWindowPlacement();

        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnClosing;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        var presenterState = (sender.Presenter as OverlappedPresenter)?.State;
        var stateChanged = presenterState != _lastPresenterState;
        if (presenterState is { } state)
        {
            _lastPresenterState = state;
        }

        CaptureNormalPlacement();
        if (args.DidPositionChange)
        {
            if (!_initializingSize && _pendingAutomaticSize is null)
            {
                _preservePosition = true;
            }

            UpdateMinimumSize();
        }

        if (!args.DidSizeChange || sender.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            return;
        }

        if (stateChanged)
        {
            return;
        }

        if (_initializingSize)
        {
            if (_pendingAutomaticSize is { } initialSize && sender.Size == initialSize)
            {
                _pendingAutomaticSize = null;
                _initializingSize = !_pageLoaded;
            }

            return;
        }

        if (_pendingAutomaticSize is { } expected && sender.Size == expected)
        {
            _pendingAutomaticSize = null;
            return;
        }

        _pendingAutomaticSize = null;
        _userResized = true;
    }

    private void ResizeForContent(int requestedWidth, int requestedContentHeight)
    {
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored } || _userResized)
        {
            return;
        }

        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var scale = Math.Max(1, ContentPage.XamlRoot?.RasterizationScale ?? 1);
        var maximumWidth = Math.Max(1, Math.Min(
            workArea.Width - 32,
            (int)Math.Round(MaximumAutomaticWidth * scale)));
        var maximumHeight = Math.Max(1, Math.Min(
            workArea.Height - 32,
            (int)Math.Round(MaximumAutomaticHeight * scale)));
        var minimumWidth = Math.Min(maximumWidth, (int)Math.Ceiling(MinimumWidth * scale));
        var minimumHeight = Math.Min(maximumHeight, (int)Math.Ceiling(MinimumHeight * scale));
        var titleBarHeight = Math.Max(48, AppTitleBar.ActualHeight);
        var requestedHeight = requestedContentHeight + titleBarHeight;
        var width = Math.Clamp((int)Math.Round(requestedWidth * scale), minimumWidth, maximumWidth);
        var height = Math.Clamp((int)Math.Round(requestedHeight * scale), minimumHeight, maximumHeight);
        var currentSize = AppWindow.Size;
        var currentPosition = AppWindow.Position;
        var x = _preservePosition ? currentPosition.X : currentPosition.X + ((currentSize.Width - width) / 2);
        var y = _preservePosition ? currentPosition.Y : currentPosition.Y + ((currentSize.Height - height) / 2);
        x = Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - width));
        y = Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - height));

        var requestedSize = new SizeInt32(width, height);
        if (requestedSize == currentSize)
        {
            if (_pageLoaded)
            {
                _initializingSize = false;
            }

            return;
        }

        _pendingAutomaticSize = requestedSize;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void UpdateMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var scale = Math.Max(1, ContentPage.XamlRoot?.RasterizationScale ?? 1);
        presenter.PreferredMinimumWidth = Math.Min(
            displayArea.WorkArea.Width,
            (int)Math.Ceiling(MinimumWidth * scale));
        presenter.PreferredMinimumHeight = Math.Min(
            displayArea.WorkArea.Height,
            (int)Math.Ceiling(MinimumHeight * scale));
    }

    public event Action? QuitRequested;

    public MainPage MainPage => ContentPage;

    public bool HeadlessModeEnabled { get; set; } = true;

    public void Show()
    {
        AppWindow.Show(activateWindow: true);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (_restoreMaximized || presenter.State == OverlappedPresenterState.Minimized && _wasMaximized)
            {
                presenter.Maximize();
            }
            else if (presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            _restoreMaximized = false;
        }

        Activate();
        var activated = SetForegroundWindow(WindowNative.GetWindowHandle(this));
        AppLog.Write($"Window shown. Foreground activation accepted: {activated}.");
        ContentPage.RequestCurrentSize();
    }

    public void CloseForQuit()
    {
        SaveWindowPlacement();
        _allowClose = true;
        Close();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowPlacement();
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (HeadlessModeEnabled)
        {
            sender.Hide();
        }
        else
        {
            QuitRequested?.Invoke();
        }
    }

    private void RestoreWindowPlacement()
    {
        try
        {
            if (_placementStore.Load() is not { } saved)
            {
                return;
            }

            var displayArea = DisplayArea.GetFromRect(
                new RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.Nearest);
            var workArea = displayArea.WorkArea;
            AppWindow.Move(new PointInt32(workArea.X, workArea.Y));
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            var placement = saved.Restore(workArea.X, workArea.Y, workArea.Width, workArea.Height, dpi,
                (int)Math.Ceiling(MinimumWidth * dpi / 96d), (int)Math.Ceiling(MinimumHeight * dpi / 96d));
            AppWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));
            _normalPlacement = placement;
            _userResized = saved.UserResized;
            _preservePosition = true;
            _restoreMaximized = saved.Maximized;
            _wasMaximized = saved.Maximized;
            UpdateMinimumSize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write(exception);
        }
    }

    private void CaptureNormalPlacement()
    {
        if (!AppWindow.IsVisible || AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        if (presenter.State == OverlappedPresenterState.Restored)
        {
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            _normalPlacement = new WindowPlacement(position.X, position.Y, size.Width, size.Height,
                GetDpiForWindow(WindowNative.GetWindowHandle(this)));
            _wasMaximized = false;
        }
        else if (presenter.State == OverlappedPresenterState.Maximized)
        {
            _wasMaximized = true;
        }
    }

    private void SaveWindowPlacement()
    {
        if (!AppWindow.IsVisible)
        {
            return;
        }

        CaptureNormalPlacement();
        if (_normalPlacement is { IsValid: true } placement)
        {
            try
            {
                _placementStore.Save(placement with { Maximized = _wasMaximized, UserResized = _userResized });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Write(exception);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

}
