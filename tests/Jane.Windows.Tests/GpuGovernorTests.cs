using System.Runtime.InteropServices;
using Jane.Core.Abstractions;
using Jane.Core.Settings;
using Jane.Windows.Gpu;

namespace Jane.Windows.Tests;

/// <summary>
/// The three sensors, against the real shell and the real driver.
/// </summary>
/// <remarks>
/// The routing *policy* is tested in Jane.Core.Tests with synthetic readings. This file tests the
/// part that cannot be faked: whether the sensors actually see what they are supposed to see on
/// this machine.
/// </remarks>
// Serialised: every test in here needs exclusive ownership of the desktop foreground, and
// running them alongside each other means they take it from one another and all skip.
[Collection("Foreground")]
public sealed partial class GpuGovernorTests
{
    [Fact]
    public void BorderlessWindowCoveringTheMonitorFiresTheGeometrySignal()
    {
        // The signal that matters most. SHQueryUserNotificationState only reports D3D *exclusive*
        // fullscreen, and the overwhelming majority of modern games are borderless-windowed, so
        // without this check the governor would miss the game entirely and load a model onto a
        // contended GPU mid-firefight.
        using var borderless = BorderlessWindow.CoverPrimaryMonitor();
        Assert.SkipWhen(borderless is null, "No interactive desktop: could not create a foreground window.");

        var detector = new FullscreenDetector();
        var (signals, process) = detector.Detect();

        Assert.True(signals.HasFlag(GameSignals.FullscreenGeometry),
            "A borderless window covering the whole monitor did not fire the geometry signal.");
        Assert.False(string.IsNullOrEmpty(process));
    }

    [Fact]
    public void AnOrdinarySizedWindowDoesNotFireTheGeometrySignal()
    {
        // The other half of the claim. A signal that fires for every window would route every
        // dictation to LLM-off and the formatting layer would never run at all.
        using var small = BorderlessWindow.Sized(120, 120, 640, 400);
        Assert.SkipWhen(small is null, "No interactive desktop: could not create a foreground window.");

        var (signals, _) = new FullscreenDetector().Detect();

        Assert.False(signals.HasFlag(GameSignals.FullscreenGeometry),
            "A 640x400 window was reported as covering the monitor.");
    }

    [Fact]
    public void GovernorRoutesToLlmOffWhileABorderlessWindowCoversTheMonitor()
    {
        using var borderless = BorderlessWindow.CoverPrimaryMonitor();
        Assert.SkipWhen(borderless is null, "No interactive desktop: could not create a foreground window.");

        // Every threshold-based signal is turned off so this asserts the geometry path alone,
        // rather than passing because the machine happened to be short of VRAM.
        var settings = new GpuSettings { TrustNvml = false, TrustNotificationState = false };
        using var governor = new GpuGovernor(settings, InGameBehaviour.SkipLlm);

        var decision = governor.Decide();

        Assert.Equal(LlmRoute.Skip, decision.Route);
        Assert.Equal(GameSignals.FullscreenGeometry, decision.Signals);
    }

    [Fact]
    public void GovernorRoutesToTheCpuModelWhenTheUserOptedIn()
    {
        using var borderless = BorderlessWindow.CoverPrimaryMonitor();
        Assert.SkipWhen(borderless is null, "No interactive desktop: could not create a foreground window.");

        var settings = new GpuSettings { TrustNvml = false, TrustNotificationState = false };
        using var governor = new GpuGovernor(settings, InGameBehaviour.UseCpuLlm);

        Assert.Equal(LlmRoute.Cpu, governor.Decide().Route);
    }

    [Fact]
    public void DetectorNeverThrows_WhateverTheDesktopIsDoing()
    {
        // Called on every key-down. A sensor that throws would take a dictation with it, and the
        // information it provides is an optimisation, not a requirement.
        var detector = new FullscreenDetector();

        for (var i = 0; i < 5; i++)
        {
            var (signals, _) = detector.Detect();
            Assert.True(Enum.IsDefined(typeof(GameSignals), (int)signals) || signals >= 0);
        }
    }

    [Fact]
    public void GovernorCachesReadingsSoKeyDownIsCheap()
    {
        using var governor = new GpuGovernor(new GpuSettings(), InGameBehaviour.SkipLlm);

        var first = governor.Decide();
        var second = governor.Decide();

        // Same instance, not merely equal: the cache is what keeps repeated consultations within
        // one dictation free.
        Assert.Same(first, second);

        governor.Invalidate();
        Assert.NotSame(first, governor.Decide());
    }

    [Fact]
    public void GovernorReportsRealVramWhenNvmlIsPresent()
    {
        using var governor = new GpuGovernor(new GpuSettings(), InGameBehaviour.SkipLlm);
        var decision = governor.Decide();

        Assert.SkipWhen(decision.FreeVramBytes is null, "NVML is not available on this machine.");
        Assert.True(decision.FreeVramBytes > 0);
        Assert.True(decision.GpuUtilisationPercent <= 100);
    }

    /// <summary>A real borderless top-level window, sized to whatever the test needs.</summary>
    private sealed partial class BorderlessWindow : IDisposable
    {
        // One window class for the whole process, with a delegate that is never collected.
        //
        // A per-instance class was the obvious shape and is wrong twice over: the delegate handed
        // to RegisterClassEx is reached only through a raw function pointer, which the runtime does
        // not treat as a reference, and a class name derived from anything reusable (a managed
        // thread id, the process id) gets *reused* by a later instance while pointing at the first
        // instance's collected delegate. Both faults present identically -- "A callback was made on
        // a garbage collected delegate" from inside CreateWindowEx.
        private static readonly WndProc SharedProc = StaticWindowProcedure;
        private static readonly nint SharedProcPointer = Marshal.GetFunctionPointerForDelegate(SharedProc);
        private static readonly Lock ClassGate = new();
        private static string? _registeredClass;

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;

        private nint _window;
        private volatile bool _running = true;

        private BorderlessWindow(int x, int y, int width, int height)
        {
            (_x, _y, _width, _height) = (x, y, width, height);

            _thread = new Thread(Pump) { IsBackground = true, Name = "Jane governor test window" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public static BorderlessWindow? CoverPrimaryMonitor()
        {
            // The whole monitor, not the work area: a borderless game covers the taskbar, and
            // that difference is exactly what separates it from a maximised ordinary window.
            var width = GetSystemMetrics(SmCxScreen);
            var height = GetSystemMetrics(SmCyScreen);
            return width > 0 && height > 0 ? Create(0, 0, width, height) : null;
        }

        public static BorderlessWindow? Sized(int x, int y, int width, int height) =>
            Create(x, y, width, height);

        private static BorderlessWindow? Create(int x, int y, int width, int height)
        {
            var window = new BorderlessWindow(x, y, width, height);

            if (!window._ready.Wait(TimeSpan.FromSeconds(10)) || window._window == 0)
            {
                window.Dispose();
                return null;
            }

            // Windows refuses SetForegroundWindow when another process owns the foreground lock,
            // which happens constantly when several tests each create a window. Retrying with a
            // BringWindowToTop in between is the documented-in-practice way through it; failing
            // that, the test skips rather than asserting against someone else's window.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                SetForegroundWindow(window._window);
                BringWindowToTop(window._window);
                Thread.Sleep(120);

                if (GetForegroundWindow() == window._window)
                {
                    return window;
                }
            }

            return Fail(window);

            static BorderlessWindow? Fail(BorderlessWindow window)
            {
                window.Dispose();
                return null;
            }
        }

        /// <summary>Registers the shared class once, and returns its name.</summary>
        private static string EnsureClassRegistered(nint instance)
        {
            lock (ClassGate)
            {
                if (_registeredClass is not null)
                {
                    return _registeredClass;
                }

                const string Name = "JaneGovernorTestWindow";
                var windowClass = new WndClassExW
                {
                    cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
                    lpfnWndProc = SharedProcPointer,
                    hInstance = instance,
                    lpszClassName = Name,
                };

                _ = RegisterClassExW(ref windowClass);
                _registeredClass = Name;
                return Name;
            }
        }

        private static nint StaticWindowProcedure(nint hWnd, uint message, nint wParam, nint lParam)
        {
            // WM_DESTROY rather than WM_CLOSE: DefWindowProc turns the close into a destroy, and
            // quitting on the destroy means the pump ends however the window went away.
            if (message == WmDestroy)
            {
                PostQuitMessage(0);
                return 0;
            }

            return DefWindowProcW(hWnd, message, wParam, lParam);
        }

        private void Pump()
        {
            var instance = GetModuleHandleW(null);
            var name = EnsureClassRegistered(instance);

            // WS_POPUP with no border is what a borderless-windowed game actually creates.
            _window = CreateWindowExW(
                WsExTopmost, name, "Jane governor test", WsPopup | WsVisible,
                _x, _y, _width, _height, 0, 0, instance, 0);

            _ready.Set();

            while (_running && GetMessageW(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
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

        private const uint WmClose = 0x0010;
        private const uint WmDestroy = 0x0002;
        private const uint WsPopup = 0x80000000;
        private const uint WsVisible = 0x10000000;
        private const uint WsExTopmost = 0x00000008;
        private const int SmCxScreen = 0;
        private const int SmCyScreen = 1;

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
        private static extern int GetMessageW(out Msg message, nint hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint DispatchMessageW(ref Msg message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint DefWindowProcW(nint hWnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PostMessageW(nint hWnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(nint hWnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint GetModuleHandleW(string? moduleName);
    }
}
