using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jane.Core.Abstractions;

namespace Jane.Windows.Hotkeys;

/// <summary>
/// A <c>WH_KEYBOARD_LL</c> hook that reports hold-to-talk state for a bare modifier key.
/// </summary>
/// <remarks>
/// <c>RegisterHotKey</c> cannot express Jane's default binding: it needs a modifier bit plus a
/// real key, and it fires once on key-down rather than reporting a held state. A low-level hook
/// can do both, at the price of two rules that are not negotiable.
/// <list type="number">
/// <item><b>The callback enqueues and returns.</b> No allocation, no I/O, no locks. Windows
/// removes a hook whose callback exceeds <c>LowLevelHooksTimeout</c> and tells nobody, so a GC
/// pause or a blocked lock inside the proc silently kills the hotkey.</item>
/// <item><b>The key is never swallowed.</b> Right Ctrl reaches every other app exactly as
/// before, or Jane would break push-to-talk in every voice chat on the machine. Esc is the sole
/// exception, and only while there is a dictation to cancel.</item>
/// </list>
/// Three threads are involved: the caller's (which only starts and stops), a hook thread that
/// owns the hook and pumps its message queue, and a pump thread that drains the ring and raises
/// events. Keeping the pump off the hook thread is what stops a slow subscriber from becoming a
/// hook-callback overrun.
/// <para>
/// It also counts, and only counts, the keys and mouse buttons the user presses. Automatic spacing
/// leans on Jane's memory of what it last typed, and that memory is only good until somebody
/// touches the window. A second hook, <c>WH_MOUSE_LL</c>, lives on the same thread under the same
/// two rules, because a click moves a caret just as surely as a key does.
/// </para>
/// </remarks>
public sealed partial class LowLevelKeyboardHook : IHotkeyListener, IKeyboardHookHandle, IUserActivityMonitor
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;

    // KBDLLHOOKSTRUCT: vkCode, scanCode, flags. MSLLHOOKSTRUCT: pt (two LONGs), mouseData, flags.
    private const int KeyboardFlagsOffset = 8;
    private const int MouseFlagsOffset = 12;
    private const int LlkhfInjected = 0x10;
    private const int LlmhfInjected = 0x01;

    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const int HcAction = 0;
    private const nint WmKeyDown = 0x0100;
    private const nint WmSysKeyDown = 0x0104;
    private const uint WmQuit = 0x0012;

    // WM_USER + 1. Only ever posted to the hook thread, which owns the whole message queue.
    private const uint WmReinstallHook = 0x0401;

    // The hook proc is [UnmanagedCallersOnly] and so cannot capture anything. Jane installs
    // exactly one hook, so a single static owner is honest rather than a limitation.
    private static LowLevelKeyboardHook? s_active;

    private readonly RawKeyEventQueue _queue;
    private readonly HotkeyStateMachine _machine;
    private readonly Action<Core.Abstractions.HotkeyEvent> _emit;
    private readonly ManualResetEventSlim _hookReady = new(initialState: false);
    private readonly Lock _gate = new();
    private readonly long _callbackBudgetTicks;

    private HotkeyOptions _options;
    private Thread? _hookThread;
    private Thread? _pumpThread;
    private RebindRequest? _pendingRebind;
    private HotkeyOptions? _pendingOptions;
    private uint _hookThreadId;
    private nint _hookHandle;
    private nint _mouseHookHandle;
    private long _longestCallbackTicks;
    private long _userActivity;
    private int _dictationActive;
    private int _pipelineActive;

    /// <summary>Whether the last Esc key-down was hidden, so its key-up is hidden with it.</summary>
    private int _swallowedEscapeDown;
    private volatile bool _stopping;
    private volatile bool _held;
    private bool _disposed;

    public LowLevelKeyboardHook(
        HotkeyBinding? binding = null,
        HotkeyMode mode = HotkeyMode.Hold,
        HotkeyOptions? options = null)
    {
        Binding = binding ?? HotkeyBinding.Default;
        Mode = mode;
        _options = options ?? new HotkeyOptions();
        _queue = new RawKeyEventQueue();
        _machine = new HotkeyStateMachine(Binding, Mode, _options);
        _emit = e => HotkeyEvent?.Invoke(this, e);
        _callbackBudgetTicks = (long)(_options.LowLevelHooksTimeout.TotalSeconds * Stopwatch.Frequency);
        Watchdog = new HookWatchdog(this, _options.WatchdogInterval);
    }

    public HotkeyBinding Binding { get; private set; }

    public HotkeyMode Mode { get; private set; }

    public bool IsHeld => _held;

    /// <inheritdoc />
    public long Version => Interlocked.Read(ref _userActivity);

    /// <summary>
    /// Re-installs the hook when Windows drops it. Owned here rather than by the caller so the
    /// protection cannot be forgotten at composition time.
    /// </summary>
    public HookWatchdog Watchdog { get; }

    /// <summary>Events dropped because the pump stalled. Non-zero is worth logging.</summary>
    public int DroppedEventCount => _queue.DroppedCount;

    public event EventHandler<Core.Abstractions.HotkeyEvent>? HotkeyEvent;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hookThread is not null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref s_active, this, null) is not null)
            {
                throw new InvalidOperationException(
                    "A LowLevelKeyboardHook is already running in this process; only one may be installed.");
            }

            _stopping = false;
            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "Jane hotkey hook",
            };
            _hookThread.Start();
            _hookReady.Wait();

            _pumpThread = new Thread(PumpThreadMain)
            {
                IsBackground = true,
                Name = "Jane hotkey pump",
            };
            _pumpThread.Start();

            Watchdog.Start();
        }
    }

    /// <summary>The thresholds and cadences in force, after any <see cref="Reconfigure"/>.</summary>
    public HotkeyOptions Options => Volatile.Read(ref _options);

    public void Rebind(HotkeyBinding binding, HotkeyMode mode)
    {
        ArgumentNullException.ThrowIfNull(binding);

        Binding = binding;
        Mode = mode;

        if (_pumpThread is null)
        {
            _machine.Rebind(binding, mode);
            return;
        }

        // The state machine belongs to the pump thread; hand the change over rather than
        // mutating it from whichever thread the settings window happens to be on.
        Interlocked.Exchange(ref _pendingRebind, new RebindRequest(binding, mode));
    }

    /// <summary>
    /// Changes the hold thresholds without disturbing the installed hook.
    /// </summary>
    /// <remarks>
    /// The pump interval and the watchdog cadence are deliberately not settable: they are
    /// internal tuning rather than anything a user has an opinion about, and changing the pump
    /// interval underneath a running pump thread would need a second hand-off for no benefit.
    /// </remarks>
    public void Reconfigure(TimeSpan minimumHold, TimeSpan maxDuration)
    {
        var updated = Options with { MinimumHold = minimumHold, MaxDuration = maxDuration };
        Volatile.Write(ref _options, updated);

        if (_pumpThread is null)
        {
            _machine.Reconfigure(updated);
            return;
        }

        Interlocked.Exchange(ref _pendingOptions, updated);
    }

    /// <summary>
    /// Tells the hook that a dictation is still being processed after key-up, so Esc keeps being
    /// consumed through Transcribing rather than reaching the app underneath.
    /// </summary>
    /// <remarks>Phase 5's orchestrator owns the wider notion of "busy"; this hook only knows
    /// about the key itself.</remarks>
    public void NotifyPipelineActive(bool active) =>
        Volatile.Write(ref _pipelineActive, active ? 1 : 0);

    /// <summary>
    /// The whole body of the hook procedure, minus the Win32 plumbing around it.
    /// </summary>
    /// <returns>
    /// True if the key must be swallowed. Only ever true for Esc during a dictation, and for the
    /// key-up that matches such a key-down; everything else -- the bound key included -- falls
    /// through to <c>CallNextHookEx</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Factored out so the "no allocation" and "does not swallow the key" rules can be asserted
    /// directly, without installing a hook or pressing a key. Every operation below is a field
    /// read, a struct copy or an ordered write.
    /// </para>
    /// <para>
    /// The down and the up are swallowed as a pair, and that is the whole reason this is not a
    /// one-line predicate. Cancelling a dictation clears the active flag between the two, so a
    /// rule that only looked at the current state swallowed the down and delivered the up -- and
    /// an application that receives a key-up it never saw pressed is entitled to do anything at
    /// all with it. A key Jane hides, it hides completely.
    /// </para>
    /// </remarks>
    /// <param name="injected">
    /// The event came from <c>SendInput</c> rather than a keyboard. Jane's own injection arrives
    /// here like everything else, and must not be mistaken for the user typing.
    /// </param>
    public bool RecordHookEvent(int virtualKey, bool isKeyDown, long timestamp, bool injected = false)
    {
        _queue.TryEnqueue(new RawKeyEvent(virtualKey, isKeyDown, timestamp));

        if (isKeyDown && !injected && !IsPartOfBinding(virtualKey))
        {
            Interlocked.Increment(ref _userActivity);
        }

        if (virtualKey != HotkeyBinding.VkEscape)
        {
            return false;
        }

        if (!isKeyDown)
        {
            // Consumed, so a second up with no down of its own -- a key already held when Jane
            // started, or a repeat the hook missed -- is delivered rather than silently eaten.
            return Interlocked.Exchange(ref _swallowedEscapeDown, 0) != 0;
        }

        var swallow = Volatile.Read(ref _dictationActive) != 0 || Volatile.Read(ref _pipelineActive) != 0;
        Volatile.Write(ref _swallowedEscapeDown, swallow ? 1 : 0);
        return swallow;
    }

    /// <summary>
    /// The whole body of the mouse hook procedure. Counts button presses and nothing else.
    /// </summary>
    /// <remarks>
    /// Movement and the wheel are ignored: neither moves a caret, and movement arrives hundreds of
    /// times a second. Nothing is ever swallowed and nothing about the event is kept -- not the
    /// position, not the button.
    /// </remarks>
    public void RecordMouseEvent(int message, bool injected)
    {
        if (injected)
        {
            return;
        }

        if (message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown)
        {
            Interlocked.Increment(ref _userActivity);
        }
    }

    /// <summary>
    /// Whether a key is the dictation hotkey or one of the modifiers it requires.
    /// </summary>
    /// <remarks>
    /// Indexed rather than enumerated: this runs inside the hook procedure, where an enumerator
    /// would be an allocation.
    /// </remarks>
    private bool IsPartOfBinding(int virtualKey)
    {
        var binding = Binding;
        if (virtualKey == binding.VirtualKey)
        {
            return true;
        }

        var modifiers = binding.RequiresModifiers;
        for (var i = 0; i < modifiers.Count; i++)
        {
            if (modifiers[i] == virtualKey)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// False once Windows has dropped the hook, or once a callback was measured beyond
    /// <see cref="HotkeyOptions.LowLevelHooksTimeout"/> -- which is the same thing, just noticed
    /// from this side.
    /// </summary>
    public bool IsInstalled =>
        Volatile.Read(ref _hookHandle) != 0
        && Volatile.Read(ref _longestCallbackTicks) < _callbackBudgetTicks;

    public bool Reinstall()
    {
        var threadId = Volatile.Read(ref _hookThreadId);
        if (threadId == 0 || _stopping)
        {
            return false;
        }

        // SetWindowsHookEx associates the hook with the calling thread, so the re-install has to
        // happen on the thread that owns it -- hence a posted message rather than a direct call.
        return PostThreadMessageW(threadId, WmReinstallHook, 0, 0);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping = true;
            Watchdog.Dispose();

            var threadId = Volatile.Read(ref _hookThreadId);
            if (threadId != 0)
            {
                PostThreadMessageW(threadId, WmQuit, 0, 0);
            }

            _hookThread?.Join(TimeSpan.FromSeconds(2));
            _pumpThread?.Join(TimeSpan.FromSeconds(2));
            _hookThread = null;
            _pumpThread = null;

            Interlocked.CompareExchange(ref s_active, null, this);
            _hookReady.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        var hook = s_active;
        if (hook is null || nCode != HcAction)
        {
            return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }

        var started = Stopwatch.GetTimestamp();

        // KBDLLHOOKSTRUCT begins with a DWORD vkCode, so this is the whole of the marshalling.
        // Letting the runtime marshal the struct would allocate, which is exactly what is banned.
        var virtualKey = Marshal.ReadInt32(lParam);
        var isKeyDown = wParam == WmKeyDown || wParam == WmSysKeyDown;
        var injected = (Marshal.ReadInt32(lParam, KeyboardFlagsOffset) & LlkhfInjected) != 0;

        var swallow = hook.RecordHookEvent(virtualKey, isKeyDown, started, injected);
        hook.RecordCallbackDuration(Stopwatch.GetTimestamp() - started);

        return swallow ? 1 : CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint MouseHookProc(int nCode, nint wParam, nint lParam)
    {
        var hook = s_active;
        if (hook is not null && nCode == HcAction)
        {
            var started = Stopwatch.GetTimestamp();
            var injected = (Marshal.ReadInt32(lParam, MouseFlagsOffset) & LlmhfInjected) != 0;

            hook.RecordMouseEvent((int)wParam, injected);
            hook.RecordCallbackDuration(Stopwatch.GetTimestamp() - started);
        }

        // Never swallowed, whatever happened above.
        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private void RecordCallbackDuration(long ticks)
    {
        if (ticks > Volatile.Read(ref _longestCallbackTicks))
        {
            Volatile.Write(ref _longestCallbackTicks, ticks);
        }
    }

    private void HookThreadMain()
    {
        Volatile.Write(ref _hookThreadId, GetCurrentThreadId());
        InstallHook();
        _hookReady.Set();

        // Thread messages carry no window, so they are handled here rather than dispatched.
        while (!_stopping && GetMessageW(out var message, nint.Zero, 0, 0) > 0)
        {
            if (message.Message == WmReinstallHook)
            {
                InstallHook();
            }
        }

        RemoveHook();
        Volatile.Write(ref _hookThreadId, 0);
    }

    private void PumpThreadMain()
    {
        while (!_stopping)
        {
            var rebind = Interlocked.Exchange(ref _pendingRebind, null);
            if (rebind is not null)
            {
                _machine.Rebind(rebind.Binding, rebind.Mode);
            }

            var options = Interlocked.Exchange(ref _pendingOptions, null);
            if (options is not null)
            {
                _machine.Reconfigure(options);
            }

            while (_queue.TryDequeue(out var raw))
            {
                _machine.Handle(raw, _emit);
            }

            _machine.Tick(Stopwatch.GetTimestamp(), _emit);

            _held = _machine.IsHeld;
            Volatile.Write(ref _dictationActive, _machine.IsActive ? 1 : 0);

            Thread.Sleep(_options.PumpInterval);
        }
    }

    private unsafe void InstallHook()
    {
        RemoveHook();

        var proc = (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&HookProc;
        var handle = SetWindowsHookExW(WhKeyboardLl, proc, GetModuleHandleW(nint.Zero), 0);

        // Best effort. Without it a click goes unnoticed and spacing is what it was before the
        // monitor existed; the hotkey itself does not depend on it, so a refusal is not a failure.
        var mouseProc = (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&MouseHookProc;
        var mouseHandle = SetWindowsHookExW(WhMouseLl, mouseProc, GetModuleHandleW(nint.Zero), 0);

        Volatile.Write(ref _hookHandle, handle);
        Volatile.Write(ref _mouseHookHandle, mouseHandle);
        Volatile.Write(ref _longestCallbackTicks, 0);
    }

    private void RemoveHook()
    {
        var handle = Interlocked.Exchange(ref _hookHandle, nint.Zero);
        if (handle != nint.Zero)
        {
            UnhookWindowsHookEx(handle);
        }

        var mouseHandle = Interlocked.Exchange(ref _mouseHookHandle, nint.Zero);
        if (mouseHandle != nint.Zero)
        {
            UnhookWindowsHookEx(mouseHandle);
        }
    }

    private sealed record RebindRequest(HotkeyBinding Binding, HotkeyMode Mode);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, nint lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(out NativeMessage lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint lpModuleName);
}
