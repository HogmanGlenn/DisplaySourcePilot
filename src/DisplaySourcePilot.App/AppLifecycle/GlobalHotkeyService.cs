using System.ComponentModel;
using System.Runtime.InteropServices;
using DisplaySourcePilot.Core.Configuration;
using Microsoft.UI.Dispatching;

namespace DisplaySourcePilot.App.AppLifecycle;

internal sealed class GlobalHotkeyService : IDisposable
{
    private const uint ModifierAlt = 0x0001;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierShift = 0x0004;
    private const uint ModifierNoRepeat = 0x4000;
    private const uint MessageHotkey = 0x0312;
    private const uint MessageQuit = 0x0012;
    private const int HdmiHotkeyId = 1;
    private const int DisplayPortHotkeyId = 2;
    private readonly DispatcherQueue _dispatcher;
    private readonly HotkeySettings _settings;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private uint _threadId;
    private Exception? _startupFailure;

    public GlobalHotkeyService(DispatcherQueue dispatcher, HotkeySettings settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _thread = new Thread(Run) { IsBackground = true, Name = "DisplaySourcePilot hotkeys" };
    }

    public event Action? HdmiPressed;

    public event Action? DisplayPortPressed;

    public void Start()
    {
        _thread.Start();
        _started.Wait();
        if (_startupFailure is not null)
        {
            throw new InvalidOperationException(_startupFailure.Message, _startupFailure);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, MessageQuit, UIntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _started.Dispose();
    }

    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            RegisterHotkeys();
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
        }
        finally
        {
            _started.Set();
        }

        if (_startupFailure is not null)
        {
            return;
        }

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message != MessageHotkey)
            {
                continue;
            }

            var id = checked((int)message.WParam);
            _dispatcher.TryEnqueue(() =>
            {
                if (id == HdmiHotkeyId)
                {
                    HdmiPressed?.Invoke();
                }
                else if (id == DisplayPortHotkeyId)
                {
                    DisplayPortPressed?.Invoke();
                }
            });
        }

        UnregisterHotKey(IntPtr.Zero, HdmiHotkeyId);
        UnregisterHotKey(IntPtr.Zero, DisplayPortHotkeyId);
    }

    private void RegisterHotkeys()
    {
        if (!_settings.IsValid)
        {
            throw new InvalidOperationException("Choose two different keyboard shortcuts.");
        }

        if (!TryRegister(_settings.Hdmi, HdmiHotkeyId, out var error))
        {
            throw new Win32Exception(error, $"{_settings.Hdmi.DisplayText} is already in use.");
        }

        if (TryRegister(_settings.DisplayPort, DisplayPortHotkeyId, out error))
        {
            return;
        }

        UnregisterHotKey(IntPtr.Zero, HdmiHotkeyId);
        throw new Win32Exception(error, $"{_settings.DisplayPort.DisplayText} is already in use.");
    }

    private static bool TryRegister(HotkeyBinding binding, int id, out int error)
    {
        var key = char.ToUpperInvariant(binding.Key[0]);
        if (RegisterHotKey(IntPtr.Zero, id, ToNativeModifiers(binding.Modifiers), key))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    private static uint ToNativeModifiers(HotkeyModifiers modifiers)
    {
        var native = modifiers switch
        {
            HotkeyModifiers.ControlAlt => ModifierControl | ModifierAlt,
            HotkeyModifiers.ControlShift => ModifierControl | ModifierShift,
            HotkeyModifiers.AltShift => ModifierAlt | ModifierShift,
            HotkeyModifiers.ControlAltShift => ModifierControl | ModifierAlt | ModifierShift,
            _ => throw new ArgumentOutOfRangeException(nameof(modifiers))
        };
        return native | ModifierNoRepeat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
