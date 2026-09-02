namespace Jane.Windows.Hotkeys;

/// <summary>The bit of a keyboard hook the watchdog needs, so it can be tested without one.</summary>
public interface IKeyboardHookHandle
{
    /// <summary>False once Windows has dropped the hook, or once a callback overran its budget.</summary>
    bool IsInstalled { get; }

    /// <summary>Re-installs the hook. Returns false if it could not be installed right now.</summary>
    bool Reinstall();
}

/// <summary>
/// Re-installs the keyboard hook after Windows silently removes it.
/// </summary>
/// <remarks>
/// A <c>WH_KEYBOARD_LL</c> callback that exceeds <c>LowLevelHooksTimeout</c> is unhooked by
/// Windows with no notification, no error and no return code -- the hotkey simply stops working
/// and nothing anywhere says why. There is no API to ask whether a hook is still live, so this
/// sweeps on a timer and re-installs whenever the hook reports itself gone.
/// <para>
/// A failed re-install is retried on the next sweep rather than treated as fatal: the usual
/// cause is a transient desktop switch, which resolves on its own.
/// </para>
/// </remarks>
public sealed class HookWatchdog : IDisposable
{
    private readonly IKeyboardHookHandle _hook;
    private readonly TimeSpan _interval;
    private readonly Lock _gate = new();

    private Timer? _timer;
    private int _reinstallCount;
    private bool _disposed;

    public HookWatchdog(IKeyboardHookHandle hook, TimeSpan interval)
    {
        _hook = hook;
        _interval = interval;
    }

    /// <summary>How many re-installs have been attempted. Surfaced so `doctor` can report it.</summary>
    public int ReinstallCount => Volatile.Read(ref _reinstallCount);

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer ??= new Timer(_ => Check(), state: null, _interval, _interval);
        }
    }

    /// <summary>
    /// One sweep. Public so the recovery rule can be asserted directly instead of by waiting on
    /// a timer.
    /// </summary>
    /// <returns>True if a re-install was attempted.</returns>
    public bool Check()
    {
        if (_hook.IsInstalled)
        {
            return false;
        }

        Interlocked.Increment(ref _reinstallCount);
        _hook.Reinstall();
        return true;
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
            _timer?.Dispose();
            _timer = null;
        }
    }
}
