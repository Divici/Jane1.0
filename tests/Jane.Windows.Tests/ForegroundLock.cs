namespace Jane.Windows.Tests;

/// <summary>
/// A machine-wide lock held while a test needs to own the desktop's foreground window.
/// </summary>
/// <remarks>
/// <para>
/// There is exactly one foreground window per desktop, and three of Jane's test projects create
/// one: the overlay focus tests, the GPU governor's borderless-window tests, and the end-to-end
/// injection harness. <c>dotnet test Jane.sln</c> runs those projects in parallel, so they take
/// the foreground from each other and fail for reasons that have nothing to do with the code
/// under test.
/// </para>
/// <para>
/// An xUnit collection cannot help: it serialises within one assembly, and this contention is
/// between processes. A named mutex is the only thing that spans them.
/// </para>
/// <para>
/// The name is deliberately global-ish but not <c>Global\</c>-prefixed -- these tests only ever
/// contend within one interactive session, and a Global mutex would need privileges the test host
/// should not be asking for.
/// </para>
/// </remarks>
internal sealed class ForegroundLock : IDisposable
{
    private const string MutexName = "Jane.Tests.ForegroundOwner";

    private readonly Mutex _mutex;
    private readonly bool _held;

    private ForegroundLock(Mutex mutex, bool held)
    {
        _mutex = mutex;
        _held = held;
    }

    /// <summary>
    /// Waits for exclusive foreground ownership, giving up after <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// A timeout does not fail the test: it proceeds without the lock, because a contended
    /// desktop is a reason for the test's own retry logic to skip, not a reason to error.
    /// </remarks>
    public static ForegroundLock Acquire(TimeSpan? timeout = null)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);

        bool held;
        try
        {
            held = mutex.WaitOne(timeout ?? TimeSpan.FromMinutes(2));
        }
        catch (AbandonedMutexException)
        {
            // A previous run was killed while holding it. The lock is ours now, and the desktop
            // state it was protecting is gone with the process that owned it.
            held = true;
        }

        return new ForegroundLock(mutex, held);
    }

    public void Dispose()
    {
        if (_held)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
