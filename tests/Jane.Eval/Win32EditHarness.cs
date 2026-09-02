using System.Diagnostics;
using System.Runtime.InteropServices;
using Jane.Core.Abstractions;
using Jane.Windows.Automation;

namespace Jane.Eval;

/// <summary>
/// A real top-level Win32 window with a real edit control, on its own thread with its own message
/// pump, used as the injection target for end-to-end tests.
/// </summary>
/// <remarks>
/// The plan's Phase 5 acceptance names Notepad. On Windows 11 that is the Store app: the process
/// started by <c>notepad.exe</c> hands off and reports <c>MainWindowHandle == 0</c>, and the text
/// area is a WinUI surface with no classic child control, so <c>WM_GETTEXT</c> cannot read it
/// back. Jane can still dictate into it perfectly well; what is impossible is *verifying* the
/// result programmatically.
/// <para>
/// This harness is that verification. It is a genuine Win32 target -- real HWND, real message
/// loop, real focus, real <c>WM_CHAR</c> delivery from <c>SendInput</c>'s <c>VK_PACKET</c>
/// path -- so everything the injector does is exercised for real; only the owner of the window is
/// the test rather than Microsoft. Phase 9 verifies against Chrome and other real applications
/// through UIA, which is the mechanism that does work across process and app-model boundaries.
/// </para>
/// </remarks>
internal sealed partial class Win32EditHarness : IDisposable
{
    private const string ClassName = "JaneTestEditHost";

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProc _wndProc;

    private nint _window;
    private nint _edit;
    private volatile bool _running = true;

    private Win32EditHarness()
    {
        // The delegate must outlive the window class, or the GC collects the thunk Windows is
        // still calling and the process dies inside user32.
        _wndProc = WindowProcedure;

        _thread = new Thread(PumpMessages) { IsBackground = true, Name = "Jane E2E edit host" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public TargetWindow Target { get; private set; } = TargetWindow.None;

    public static Win32EditHarness? Create()
    {
        var harness = new Win32EditHarness();

        if (!harness._ready.Wait(TimeSpan.FromSeconds(10)) || harness._window == 0)
        {
            harness.Dispose();
            return null;
        }

        harness.Focus();
        harness.Target = new FocusedAppIdentity().GetForegroundWindow();

        // If the session refuses foreground (a locked desktop, or another app holding the
        // foreground lock), the identity will not be this window and the test must skip rather
        // than assert against someone else's window.
        return harness.Target.Handle == harness._window ? harness : Fail(harness);

        static Win32EditHarness? Fail(Win32EditHarness harness)
        {
            harness.Dispose();
            return null;
        }
    }

    public void Focus()
    {
        // Foreground from here, then focus is posted to the owning thread: SetFocus is a no-op
        // from any other thread, and SetForegroundWindow on an already-foreground window sends no
        // WM_SETFOCUS, so relying on that alone leaves the caret nowhere and SendInput silently
        // delivers to no control at all.
        SetForegroundWindow(_window);
        Thread.Sleep(200);
        PostMessageW(_window, WmFocusEdit, 0, 0);
        Thread.Sleep(200);
    }

    /// <summary>Reads the edit control's text, polling because SendInput is asynchronous.</summary>
    public async Task<string> ReadTextAsync(CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var text = string.Empty;

        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            text = ReadOnce();
            if (!string.IsNullOrWhiteSpace(text))
            {
                // One more poll: a partially delivered chunk would otherwise be read as final.
                await Task.Delay(250, cancellationToken);
                return ReadOnce();
            }

            await Task.Delay(120, cancellationToken);
        }

        return text;
    }

    private string ReadOnce()
    {
        var length = (int)SendMessageW(_edit, WmGetTextLength, 0, 0);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal((length + 1) * 2);
        try
        {
            _ = SendMessageW(_edit, WmGetText, length + 1, buffer);
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void PumpMessages()
    {
        var instance = GetModuleHandleW(null);

        var windowClass = new WndClassExW
        {
            cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = ClassName + Environment.ProcessId,
        };

        // A class registered by an earlier run in the same process is fine to reuse; any other
        // failure means no window, which Create turns into a skip.
        _ = RegisterClassExW(ref windowClass);

        _window = CreateWindowExW(
            0, windowClass.lpszClassName, "Jane end-to-end target",
            WsOverlappedWindow | WsVisible,
            120, 120, 720, 360, 0, 0, instance, 0);

        if (_window != 0)
        {
            _edit = CreateWindowExW(
                WsExClientEdge, "EDIT", string.Empty,
                WsChild | WsVisible | EsMultiline | EsAutoVScroll | EsWantReturn,
                0, 0, 700, 300, _window, 0, instance, 0);
        }

        _ready.Set();

        while (_running && GetMessageW(out var message, 0, 0, 0) > 0)
        {
            // TranslateMessage is what turns SendInput's VK_PACKET into WM_CHAR. Without it the
            // Unicode injection path would silently deliver nothing, which is precisely the
            // failure this harness exists to catch.
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private nint WindowProcedure(nint hWnd, uint message, nint wParam, nint lParam)
    {
        // SetFocus only works from the thread that owns the window, so the test thread cannot
        // call it directly. Handling WM_SETFOCUS here puts the caret in the edit control on the
        // owning thread, which is what makes SendInput deliver WM_CHAR to it at all.
        if ((message == WmSetFocus || message == WmFocusEdit) && _edit != 0)
        {
            SetFocus(_edit);
            return 0;
        }

        if (message == WmClose)
        {
            _running = false;
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProcW(hWnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        _running = false;

        if (_window != 0)
        {
            PostMessageW(_window, WmClose, 0, 0);
        }

        _thread.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
    }

    /// <summary>Private message: "put the caret in the edit control", posted from the test thread.</summary>
    private const uint WmFocusEdit = 0x8001;

    private const uint WmSetFocus = 0x0007;
    private const uint WmClose = 0x0010;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const uint WsExClientEdge = 0x00000200;
    private const uint EsMultiline = 0x0004;
    private const uint EsAutoVScroll = 0x0040;
    private const uint EsWantReturn = 0x1000;

    private delegate nint WndProc(nint hWnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassExW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassExW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out Msg message, nint hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessageW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetFocus(nint hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
