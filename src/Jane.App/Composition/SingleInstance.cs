namespace Jane.App.Composition;

/// <summary>
/// Holds the claim that this process is the only Jane.
/// </summary>
/// <remarks>
/// <para>
/// A second copy of Jane is not harmlessly redundant. Both install a low-level keyboard hook on
/// the same key, so a press reaches two pipelines; both open the capture device, so they contend
/// for the microphone and each other's warm-up; and both draw a pill at the same bottom-centre
/// anchor, which renders as two strings on top of each other. That overlap is how the problem was
/// found, and it is unmistakable once seen, because one overlay holds one text element and
/// physically cannot draw two.
/// </para>
/// <para>
/// A named mutex rather than a scan of running processes: a scan races itself. Two copies started
/// together both look, both see nothing, and both carry on. The mutex is decided by the kernel,
/// once, and the loser knows it lost.
/// </para>
/// <para>
/// Session-scoped rather than machine-wide. Jane is a per-user tray app that hooks that user's
/// keyboard and types into that user's windows, so two people signed into one machine are each
/// entitled to their own -- which is what the <c>Local\</c> prefix gives, since the kernel scopes
/// it to the terminal-services session.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The name the app uses. Tests pass their own so they never collide with a live Jane.</summary>
    public const string DefaultName = "jane-dictation-single-instance";

    /// <summary>
    /// Overrides the claimed name for a process that is deliberately not "the" Jane.
    /// </summary>
    /// <remarks>
    /// The same problem <c>JANE_HOME</c> solves, for the same reason: a test that launches a real
    /// Jane in a child process cannot pass a constructor argument across the boundary, and without
    /// an override that child loses the claim to whatever copy the developer is running and exits
    /// before it measures anything.
    /// </remarks>
    public const string EnvName = "JANE_INSTANCE";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly CancellationTokenSource? _watching;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, bool isOwner)
    {
        _mutex = mutex;
        _signal = signal;
        IsOwner = isOwner;

        if (!isOwner)
        {
            return;
        }

        _watching = new CancellationTokenSource();
        _ = Task.Factory.StartNew(
            () => Watch(_watching.Token),
            _watching.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Raised on a background thread when another copy of Jane tried to start.</summary>
    /// <remarks>
    /// Only ever raised on the owner. Handlers marshal to the UI themselves -- this deliberately
    /// knows nothing about a dispatcher, so it stays testable without one.
    /// </remarks>
    public event EventHandler? SecondInstanceAttempted;

    /// <summary>Whether this process won the claim and should carry on starting.</summary>
    public bool IsOwner { get; }

    /// <summary>
    /// Claims the name, or discovers that somebody else holds it.
    /// </summary>
    /// <remarks>
    /// Never blocks. A guard that waited for the name would leave a second copy hanging invisibly
    /// until the first quit, which is worse than either outcome it is choosing between.
    /// </remarks>
    public static SingleInstance Acquire(string? name = null)
    {
        // Explicit argument, then the environment, then the app's own name -- the same order of
        // preference JanePaths uses for the profile root.
        if (string.IsNullOrWhiteSpace(name))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(EnvName);
            name = string.IsNullOrWhiteSpace(fromEnvironment) ? DefaultName : fromEnvironment;
        }

        // Ownership is decided by who created the kernel object, not by who waited on it.
        // WaitOne is recursive per thread, so a second Acquire on one thread would wrongly succeed;
        // createdNew never does. It also fails safe: the object lives only while a handle is open,
        // so a Jane that crashes or is killed has its handle closed by Windows and frees the name,
        // where a held mutex would need the abandoned-state dance to reclaim.
        var mutex = new Mutex(initiallyOwned: false, $"Local\\{name}", out var createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}-signal");

        return new SingleInstance(mutex, signal, createdNew);
    }

    /// <summary>
    /// Tells the running copy that this one tried to start, so it can surface itself.
    /// </summary>
    /// <remarks>
    /// Called by the loser on its way out. Exiting silently is indistinguishable from the launch
    /// having done nothing at all, which is exactly the confusion that leads somebody to click the
    /// icon four more times.
    /// </remarks>
    public void NotifyOwner()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // The owner quit between the acquire and this call. Nothing to tell.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_watching is not null)
        {
            _watching.Cancel();
            _watching.Dispose();
        }

        // No ReleaseMutex: the claim is the open handle, not an acquired lock, so closing it below
        // is what hands the name back.
        _signal.Dispose();
        _mutex.Dispose();
    }

    private void Watch(CancellationToken cancellationToken)
    {
        using var stop = new ManualResetEventSlim(false);
        using var registration = cancellationToken.Register(stop.Set);

        var handles = new[] { _signal, stop.WaitHandle };

        while (!cancellationToken.IsCancellationRequested)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (index != 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SecondInstanceAttempted?.Invoke(this, EventArgs.Empty);
        }
    }
}
