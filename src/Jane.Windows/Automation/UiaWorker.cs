using System.Diagnostics;
using System.Runtime.InteropServices;
using Jane.Core.Abstractions;

namespace Jane.Windows.Automation;

/// <summary>How a single read ended.</summary>
public enum UiaReadStatus
{
    /// <summary>Something usable came back, even if only some of it.</summary>
    Ok,

    /// <summary>UI Automation could not be created in this session.</summary>
    Unavailable,

    /// <summary>Nothing had keyboard focus.</summary>
    NoFocusedElement,

    /// <summary>The focused element belongs to a different process than the dictation target.</summary>
    FocusMoved,

    /// <summary>The focused control reports <c>IsPassword</c>. Nothing was read from it.</summary>
    PasswordControl,

    /// <summary>The control exposes neither a text pattern nor a value.</summary>
    NoTextProvider,

    /// <summary>The provider returned a failure, or threw.</summary>
    ProviderError,
}

/// <param name="MaxSelectionChars">
/// The cap handed to <c>ITextRangeProvider::GetText</c>. Unbounded is the documented way to make a
/// large document slow, and Deep Context only ever needs enough text to mine terms from.
/// </param>
/// <param name="SoftBudget">
/// Checked by the session <em>between</em> round trips. It cannot interrupt one -- nothing can --
/// but it stops a read that has already spent its time from making four more calls nobody will
/// wait for. Set below the orchestrator's deadline on purpose.
/// </param>
public sealed record UiaReadRequest(TargetWindow Target)
{
    public int MaxSelectionChars { get; init; } = 4_000;

    public int MaxSurroundingChars { get; init; } = 4_000;

    public TimeSpan SoftBudget { get; init; } = TimeSpan.FromMilliseconds(60);
}

/// <summary>
/// What one read produced. Managed values only -- no COM pointer ever leaves the worker thread.
/// </summary>
/// <param name="RoundTrips">
/// How many cross-process calls the read actually cost. Recorded because the plan lists UIA
/// round-trip latency as an unknown, and a number without a call count cannot be interpreted.
/// </param>
public sealed record UiaRawRead
{
    public UiaReadStatus Status { get; init; } = UiaReadStatus.Ok;

    public string? ControlName { get; init; }

    public int ControlTypeId { get; init; }

    public string? ClassName { get; init; }

    public string? FrameworkId { get; init; }

    public int ProcessId { get; init; }

    public bool IsPassword { get; init; }

    public string? Value { get; init; }

    public string? Selection { get; init; }

    public string? Surrounding { get; init; }

    public CaretRect? Caret { get; init; }

    public int RoundTrips { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>The read stopped early because it had already spent its budget.</summary>
    public bool BudgetExpired { get; init; }

    public string? Detail { get; init; }

    public bool HasText => Selection is not null || Surrounding is not null || Value is not null;

    public static UiaRawRead Failed(UiaReadStatus status, string? detail = null) =>
        new() { Status = status, Detail = detail };
}

/// <summary>
/// One Deep Context read against the focused window.
/// </summary>
/// <remarks>
/// Exists as an interface for one reason: a wedged provider is the failure this phase is designed
/// around, and it has to be reproducible in a test without a real application that hangs. The
/// shipping implementation is <see cref="UiaComSession"/>.
/// </remarks>
public interface IUiaSession : IDisposable
{
    bool IsAvailable { get; }

    /// <summary>Blocks. May block forever -- that is the whole premise of the worker around it.</summary>
    UiaRawRead Read(UiaReadRequest request);
}

/// <summary>What the single worker thread is doing right now.</summary>
public enum UiaWorkerState
{
    Starting,
    Idle,
    Reading,

    /// <summary>Reading, and past the point where it should have finished.</summary>
    Wedged,

    /// <summary>The session could not be created. No read will ever succeed this session.</summary>
    Unavailable,

    Stopped,
}

/// <param name="WedgeAfter">
/// How long an in-flight read may run before the next key-down treats it as stuck. Matches the
/// orchestrator's deadline: a read that has already missed the deadline once is not going to be
/// useful, and one still running at the next press is the definition of wedged.
/// </param>
public sealed record UiaWorkerOptions
{
    public static UiaWorkerOptions Default { get; } = new();

    public TimeSpan WedgeAfter { get; init; } = TimeSpan.FromMilliseconds(80);

    public string ThreadName { get; init; } = "Jane UIA worker";
}

/// <summary>
/// The single, long-lived thread that every Deep Context read goes through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one thread, and never more.</b> A blocked cross-process COM call cannot be cancelled.
/// It can only be abandoned, and abandoning it leaves the thread it is on stuck until the provider
/// answers -- which may be never. So a "read with a timeout" built on <c>Task.Run</c> plus
/// <c>WaitAsync</c> is not a timeout at all: it is a thread leak, one per hang, growing for as
/// long as the session lasts. With a single worker, a hostile or broken provider costs exactly one
/// thread, once.
/// </para>
/// <para>
/// <b>What happens while it is stuck.</b> Reads submitted during a wedge are refused immediately
/// rather than queued, so the pipeline never waits on a queue behind a call that will not return.
/// Deep Context is simply off until the provider answers. If it ever does, the thread goes back to
/// work by itself -- wedged is not dead. The process that caused it stays blocklisted for the
/// session, which is what stops the same wedge happening on every press.
/// </para>
/// <para>
/// <b>Apartment.</b> MTA, deliberately. A UI Automation client that runs STA has to pump messages
/// for cross-apartment marshalling to complete, and this thread has no message loop -- an STA
/// worker would deadlock on the first cross-process call rather than merely being slow.
/// </para>
/// </remarks>
public sealed partial class UiaWorker : IDisposable
{
    private readonly Func<IUiaSession> _sessionFactory;
    private readonly UiaWorkerOptions _options;
    private readonly Thread _thread;
    private readonly SemaphoreSlim _work = new(0, 1);
    private readonly Lock _gate = new();

    private Job? _pending;
    private Job? _inFlight;
    private volatile UiaWorkerState _state = UiaWorkerState.Starting;
    private int _completed;
    private int _submitted;
    private bool _disposed;
    private volatile bool _stopping;

    /// <param name="sessionFactory">
    /// Invoked once, on the worker thread. It has to be a factory rather than an instance because
    /// a COM client must be created on the apartment that will use it.
    /// </param>
    public UiaWorker(Func<IUiaSession> sessionFactory, UiaWorkerOptions? options = null)
    {
        _sessionFactory = sessionFactory;
        _options = options ?? UiaWorkerOptions.Default;

        _thread = new Thread(Pump)
        {
            Name = _options.ThreadName,

            // Background, so a thread stuck inside a provider can never hold the process open.
            IsBackground = true,
        };

        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>The shipping configuration: one worker over a real UI Automation client.</summary>
    public static UiaWorker CreateDefault(UiaComOptions? com = null, UiaWorkerOptions? options = null) =>
        new(() => new UiaComSession(com), options);

    public UiaWorkerState State
    {
        get
        {
            var state = _state;
            return state == UiaWorkerState.Reading && InFlightFor > _options.WedgeAfter
                ? UiaWorkerState.Wedged
                : state;
        }
    }

    public bool IsWedged => State == UiaWorkerState.Wedged;

    /// <summary>The window whose read is in flight, or <c>None</c>.</summary>
    public TargetWindow InFlightTarget
    {
        get
        {
            lock (_gate)
            {
                return _inFlight?.Request.Target ?? TargetWindow.None;
            }
        }
    }

    /// <summary>How long the in-flight read has been running. Zero when idle.</summary>
    public TimeSpan InFlightFor
    {
        get
        {
            lock (_gate)
            {
                return _inFlight?.Clock.Elapsed ?? TimeSpan.Zero;
            }
        }
    }

    public int CompletedReads => Volatile.Read(ref _completed);

    public int SubmittedReads => Volatile.Read(ref _submitted);

    /// <summary>
    /// Hands a read to the worker. Returns false when it is busy -- never blocks, and never
    /// queues behind an in-flight read.
    /// </summary>
    /// <remarks>
    /// Called from whichever thread saw the key-down. Refusing rather than queueing is the point:
    /// a queue behind a wedged call would turn one hang into an unbounded backlog of dictations
    /// all waiting on the same dead provider.
    /// </remarks>
    public bool TrySubmit(UiaReadRequest request, out Task<UiaRawRead> completion)
    {
        completion = Task.FromResult(UiaRawRead.Failed(UiaReadStatus.Unavailable, "The UIA worker is not accepting reads."));

        if (_disposed || _stopping || _state is UiaWorkerState.Stopped or UiaWorkerState.Unavailable)
        {
            return false;
        }

        var job = new Job(request);

        lock (_gate)
        {
            if (_pending is not null || _inFlight is not null)
            {
                return false;
            }

            _pending = job;
        }

        Interlocked.Increment(ref _submitted);
        completion = job.Completion.Task;

        try
        {
            _work.Release();
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose. The job simply never runs; the caller sees its deadline.
            return false;
        }

        return true;
    }

    private void Pump()
    {
        // The CLR has already put this thread in the MTA. CoInitializeEx is still called so the
        // apartment is initialised before the first CoCreateInstance rather than implicitly at it,
        // and so S_FALSE / RPC_E_CHANGED_MODE are observed here rather than as a mystery later.
        _ = CoInitializeEx(0, CoinitMultithreaded);

        IUiaSession? session = null;
        try
        {
            session = _sessionFactory();
            _state = session.IsAvailable ? UiaWorkerState.Idle : UiaWorkerState.Unavailable;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deep Context is unavailable: {ex}");
            _state = UiaWorkerState.Unavailable;
        }

        try
        {
            while (!_stopping)
            {
                try
                {
                    _work.Wait();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (_stopping)
                {
                    return;
                }

                Job? job;
                lock (_gate)
                {
                    job = _pending;
                    _pending = null;
                    _inFlight = job;
                }

                if (job is null)
                {
                    continue;
                }

                if (_state != UiaWorkerState.Unavailable)
                {
                    _state = UiaWorkerState.Reading;
                }

                UiaRawRead result;
                try
                {
                    result = session is { IsAvailable: true }
                        ? session.Read(job.Request)
                        : UiaRawRead.Failed(UiaReadStatus.Unavailable, "UI Automation is not available in this session.");
                }
                catch (Exception ex)
                {
                    result = UiaRawRead.Failed(UiaReadStatus.ProviderError, ex.Message);
                }

                lock (_gate)
                {
                    _inFlight = null;
                }

                if (_state != UiaWorkerState.Unavailable)
                {
                    _state = UiaWorkerState.Idle;
                }

                Interlocked.Increment(ref _completed);

                // Asynchronous continuations: the awaiting pipeline must never run on the one
                // thread that serves every read.
                job.Completion.TrySetResult(result with { Elapsed = job.Clock.Elapsed });
            }
        }
        finally
        {
            _state = UiaWorkerState.Stopped;
            session?.Dispose();
            CoUninitialize();
        }
    }

    /// <summary>
    /// Stops the worker. Never waits on a stuck provider.
    /// </summary>
    /// <remarks>
    /// The join is bounded and its failure is ignored on purpose. A wedged thread is a background
    /// thread with no managed state anyone else can see, so leaving it blocked inside a provider
    /// costs the process nothing at shutdown -- whereas waiting for it would hang the app's exit
    /// on the same broken application that caused the problem.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;

        try
        {
            _work.Release();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SemaphoreFullException)
        {
            // Already signalled, or already gone.
        }

        _ = _thread.Join(TimeSpan.FromSeconds(1));

        lock (_gate)
        {
            _pending?.Completion.TrySetResult(UiaRawRead.Failed(UiaReadStatus.Unavailable, "The UIA worker shut down."));
            _pending = null;
        }

        _work.Dispose();
    }

    private sealed class Job(UiaReadRequest request)
    {
        public UiaReadRequest Request { get; } = request;

        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        public TaskCompletionSource<UiaRawRead> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private const uint CoinitMultithreaded = 0;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
