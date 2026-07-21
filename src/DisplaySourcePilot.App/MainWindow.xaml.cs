using System.Runtime.InteropServices;
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

        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnClosing;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange)
        {
            UpdateMinimumSize();
        }

        if (!args.DidSizeChange)
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
        if (AppWindow.Presenter is not OverlappedPresenter || _userResized)
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
        var x = currentPosition.X + ((currentSize.Width - width) / 2);
        var y = currentPosition.Y + ((currentSize.Height - height) / 2);
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
        Activate();
        AppWindow.Show();
        ContentPage.RequestCurrentSize();
    }

    public void CloseForQuit()
    {
        _allowClose = true;
        Close();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

}
