using System.Diagnostics;
using Jane.Core.Abstractions;
using Jane.Core.Vocabulary;
using Jane.Windows.Automation;

namespace Jane.Windows.Tests;

/// <summary>
/// The worker's contract: one thread, ever, and a wedge that costs a bounded amount.
/// </summary>
/// <remarks>
/// Every test here drives a substituted <see cref="IUiaSession"/> rather than real UI Automation.
/// That is the point -- a blocked cross-process COM call cannot be cancelled, only abandoned, so
/// the behaviour that matters is what the worker does <em>around</em> such a call, and that has to
/// be assertable deterministically and without a desktop. <c>ContextReaderTests</c> exercises the
/// same paths against the real provider, including a real window that stops pumping.
/// </remarks>
public sealed class UiaWorkerTests
{
    private static readonly TargetWindow Editor = new(0x1234, 4242, "someeditor", "Window", "notes.txt - Some Editor");
    private static readonly TargetWindow Other = new(0x5678, 909, "otherapp", "Window", "Other");

    [Fact]
    public async Task AReadIsServedAndTheWorkerReturnsToIdle()
    {
        var session = new ScriptedSession { Selection = "Kubernetes and Grafana" };
        using var worker = new UiaWorker(() => session);

        Assert.True(worker.TrySubmit(new UiaReadRequest(Editor), out var completion));
        var read = await completion;

        Assert.Equal(UiaReadStatus.Ok, read.Status);
        Assert.Equal("Kubernetes and Grafana", read.Selection);
        Assert.Equal(1, worker.CompletedReads);
        WaitUntil(() => worker.State == UiaWorkerState.Idle, "the worker never returned to Idle.");
    }

    [Fact]
    public async Task EverySubmittedReadIsServedBySingleLongLivedThread()
    {
        // The locked decision, asserted directly: one thread total, not one per request.
        var session = new ScriptedSession();
        using var worker = new UiaWorker(() => session);

        for (var i = 0; i < 40; i++)
        {
            Assert.True(worker.TrySubmit(new UiaReadRequest(Editor), out var completion));
            _ = await completion;
        }

        Assert.Equal(40, worker.CompletedReads);
        Assert.Single(session.ServingThreads);
        Assert.NotEqual(Environment.CurrentManagedThreadId, session.ServingThreads.Single());
    }

    [Fact]
    public void AHangingProviderReturnsEmptyAtTheDeadlineAndNeverBlocksTheCaller()
    {
        using var hang = new HangingSession();
        using var worker = new UiaWorker(() => hang);
        using var reader = new UiaContextReader(worker, new Blocklist(), Deadline(80));

        var submitted = Stopwatch.StartNew();
        var handle = reader.BeginRead(Editor);
        var submitCost = submitted.Elapsed;

        var waited = Stopwatch.StartNew();
        var read = handle.Wait();
        waited.Stop();

        // Key-down must cost nothing: the read is handed to the worker, not run on the caller.
        Assert.True(submitCost < TimeSpan.FromMilliseconds(50), $"BeginRead blocked for {submitCost.TotalMilliseconds:F0} ms.");
        Assert.Equal(ContextOutcome.TimedOut, read.Outcome);
        Assert.True(read.Hints.IsEmpty);
        Assert.True(waited.Elapsed >= TimeSpan.FromMilliseconds(70), $"Gave up after only {waited.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(2), $"Waited {waited.Elapsed.TotalMilliseconds:F0} ms, far past the deadline.");
    }

    [Fact]
    public void AHangingProviderLeaksNoThreadAcrossManyKeyDowns()
    {
        // The failure this design exists to prevent. A task-per-request "timeout" is really
        // abandonment, and would leave one stuck thread behind for every press.
        using var hang = new HangingSession();

        var before = ThreadCount();
        using (var worker = new UiaWorker(() => hang))
        using (var reader = new UiaContextReader(worker, new Blocklist(), Deadline(20)))
        {
            for (var i = 0; i < 40; i++)
            {
                _ = reader.BeginRead(Editor).Wait();
            }

            var during = ThreadCount();
            Assert.True(
                during - before <= 4,
                $"Thread count went from {before} to {during} over 40 wedged reads.");
            Assert.Equal(1, hang.Entered);
        }
    }

    [Fact]
    public void AProviderStillStuckAtTheNextKeyDownIsAutoBlocklistedForTheSession()
    {
        using var hang = new HangingSession();
        var blocklist = new Blocklist();
        using var worker = new UiaWorker(() => hang, new UiaWorkerOptions { WedgeAfter = TimeSpan.FromMilliseconds(40) });
        using var reader = new UiaContextReader(worker, blocklist, Deadline(60));

        // First key-down: the read goes out and never comes back.
        var first = reader.BeginRead(Editor).Wait();
        Assert.Equal(ContextOutcome.TimedOut, first.Outcome);

        // Second key-down, with the worker still stuck on the first.
        var second = reader.BeginRead(Editor).Wait();
        Assert.Equal(ContextOutcome.WorkerWedged, second.Outcome);
        Assert.Contains("someeditor", blocklist.AutoBlocked);

        // Third key-down: refused by the blocklist before the worker is even consulted.
        var third = reader.BeginRead(Editor).Wait();
        Assert.Equal(ContextOutcome.Blocked, third.Outcome);
        Assert.Equal(1, hang.Entered);
    }

    [Fact]
    public void AWedgeBlocklistsOnlyTheProcessThatCausedIt()
    {
        using var hang = new HangingSession();
        var blocklist = new Blocklist();
        using var worker = new UiaWorker(() => hang, new UiaWorkerOptions { WedgeAfter = TimeSpan.FromMilliseconds(40) });
        using var reader = new UiaContextReader(worker, blocklist, Deadline(60));

        _ = reader.BeginRead(Editor).Wait();
        _ = reader.BeginRead(Other).Wait();

        // The wedge belongs to the window that was being read, not to whatever was focused next.
        Assert.Contains("someeditor", blocklist.AutoBlocked);
        Assert.DoesNotContain("otherapp", blocklist.AutoBlocked);
    }

    [Fact]
    public void AReadStillRunningButNotYetWedgedIsRefusedWithoutBlocklistingAnything()
    {
        // Back-to-back dictation is legitimate. Only a read that has already blown the deadline
        // counts as stuck; a merely slow one must not cost the user Deep Context for the session.
        using var gate = new ManualResetEventSlim(false);
        var slow = new BlockingSession(gate);
        var blocklist = new Blocklist();
        using var worker = new UiaWorker(() => slow, new UiaWorkerOptions { WedgeAfter = TimeSpan.FromSeconds(30) });
        using var reader = new UiaContextReader(worker, blocklist, Deadline(40));

        _ = reader.BeginRead(Editor).Wait();
        var second = reader.BeginRead(Editor).Wait();

        Assert.Equal(ContextOutcome.WorkerBusy, second.Outcome);
        Assert.Empty(blocklist.AutoBlocked);

        gate.Set();
    }

    [Fact]
    public void TheWorkerRecoversOnceTheStuckCallFinallyReturns()
    {
        // Wedged is not dead. If the provider ever answers, the single thread goes back to work;
        // only the offending process stays blocklisted.
        using var gate = new ManualResetEventSlim(false);
        var session = new BlockingSession(gate) { Selection = "Grafana" };
        using var worker = new UiaWorker(() => session, new UiaWorkerOptions { WedgeAfter = TimeSpan.FromMilliseconds(30) });
        using var reader = new UiaContextReader(worker, new Blocklist(), Deadline(50));

        Assert.Equal(ContextOutcome.TimedOut, reader.BeginRead(Editor).Wait().Outcome);

        gate.Set();
        WaitUntil(() => worker.State == UiaWorkerState.Idle, "the worker stayed wedged after the call returned.");

        var recovered = reader.BeginRead(Other).Wait(TimeSpan.FromSeconds(2));
        Assert.Equal(ContextOutcome.Read, recovered.Outcome);
        Assert.Contains("Grafana", recovered.Hints.Terms);
    }

    [Fact]
    public void DisposingAWedgedWorkerReturnsPromptlyAndDoesNotThrow()
    {
        // Shutdown cannot wait on a call that will never return, so the worker thread is a
        // background thread and Dispose gives up on joining it.
        using var hang = new HangingSession();
        var worker = new UiaWorker(() => hang);
        using (var reader = new UiaContextReader(worker, new Blocklist(), Deadline(30)))
        {
            _ = reader.BeginRead(Editor).Wait();
        }

        var stopwatch = Stopwatch.StartNew();
        worker.Dispose();
        worker.Dispose();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Dispose took {stopwatch.Elapsed.TotalSeconds:F1} s.");
    }

    [Fact]
    public void ASessionThatCannotBeCreatedDegradesToEmptyContextRatherThanThrowing()
    {
        using var worker = new UiaWorker(static () => throw new InvalidOperationException("no UIA here"));
        using var reader = new UiaContextReader(worker, new Blocklist(), Deadline(200));

        var read = reader.BeginRead(Editor).Wait();

        Assert.Equal(ContextOutcome.ProviderUnavailable, read.Outcome);
        Assert.True(read.Hints.IsEmpty);
    }

    [Fact]
    public void AProviderThatThrowsIsReportedAsAnErrorAndTheWorkerKeepsServing()
    {
        var session = new ScriptedSession { Throws = new COMExceptionStandIn() };
        using var worker = new UiaWorker(() => session);
        using var reader = new UiaContextReader(worker, new Blocklist(), Deadline(500));

        Assert.Equal(ContextOutcome.Empty, reader.BeginRead(Editor).Wait().Outcome);

        session.Throws = null;
        session.Selection = "Kubernetes";
        Assert.Equal(ContextOutcome.Read, reader.BeginRead(Editor).Wait().Outcome);
    }

    [Fact]
    public void APasswordControlIsNeverRead()
    {
        var session = new ScriptedSession { Status = UiaReadStatus.PasswordControl, IsPassword = true, Selection = "hunter2" };
        using var worker = new UiaWorker(() => session);
        using var reader = new UiaContextReader(worker, new Blocklist(), Deadline(500));

        var read = reader.BeginRead(Editor).Wait();

        Assert.Equal(ContextOutcome.PasswordControl, read.Outcome);
        Assert.True(read.Hints.IsEmpty);
        Assert.Null(read.Hints.Selection);
    }

    [Fact]
    public void ABlocklistedProcessYieldsEmptyHintsAndMakesNoUiaCallAtAll()
    {
        var session = new ScriptedSession { Selection = "secret vault entry" };
        using var worker = new UiaWorker(() => session);
        using var reader = new UiaContextReader(worker, new Blocklist(), Deadline(500));

        var read = reader.BeginRead(new TargetWindow(1, 2, "1password", "Window", "1Password")).Wait();

        Assert.Equal(ContextOutcome.Blocked, read.Outcome);
        Assert.True(read.Hints.IsEmpty);
        Assert.Equal(0, session.Reads);
        Assert.Equal(0, worker.SubmittedReads);
    }

    [Fact]
    public void LastReadRecordsWhatWasCapturedSoSettingsCanShowIt()
    {
        var session = new ScriptedSession { Selection = "Deploy the Grafana dashboard" };
        using var worker = new UiaWorker(() => session);
        using var reader = new UiaContextReader(worker, new Blocklist(), Deadline(500));

        Assert.Equal(ContextOutcome.NotAttempted, reader.LastRead.Outcome);
        _ = reader.BeginRead(Editor).Wait();

        Assert.Equal(ContextOutcome.Read, reader.LastRead.Outcome);
        Assert.Equal("Deploy the Grafana dashboard", reader.LastRead.Hints.Selection);
        Assert.Contains("Grafana", reader.LastRead.Hints.Terms);
    }

    private static ContextReadOptions Deadline(int milliseconds) =>
        new() { Deadline = TimeSpan.FromMilliseconds(milliseconds) };

    private static int ThreadCount() => Process.GetCurrentProcess().Threads.Count;

    private static void WaitUntil(Func<bool> condition, string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail(failure);
    }

    /// <summary>A provider that answers immediately, recording which thread served each call.</summary>
    private sealed class ScriptedSession : IUiaSession
    {
        private readonly HashSet<int> _threads = [];

        public bool IsAvailable => true;

        public int Reads { get; private set; }

        public string? Selection { get; set; }

        public string? Surrounding { get; set; }

        public bool IsPassword { get; set; }

        public UiaReadStatus Status { get; set; } = UiaReadStatus.Ok;

        public Exception? Throws { get; set; }

        public IReadOnlyCollection<int> ServingThreads
        {
            get
            {
                lock (_threads)
                {
                    return [.. _threads];
                }
            }
        }

        public UiaRawRead Read(UiaReadRequest request)
        {
            lock (_threads)
            {
                _threads.Add(Environment.CurrentManagedThreadId);
            }

            Reads++;

            if (Throws is { } ex)
            {
                throw ex;
            }

            return new UiaRawRead
            {
                Status = Status,
                Selection = Selection,
                Surrounding = Surrounding,
                IsPassword = IsPassword,
                ProcessId = request.Target.ProcessId,
                RoundTrips = 1,
            };
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A provider that never returns. Released only when the test disposes it.</summary>
    private sealed class HangingSession : IUiaSession, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _entered;

        public bool IsAvailable => true;

        public int Entered => Volatile.Read(ref _entered);

        public UiaRawRead Read(UiaReadRequest request)
        {
            Interlocked.Increment(ref _entered);
            _release.Wait();
            return UiaRawRead.Failed(UiaReadStatus.ProviderError, "released at teardown");
        }

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }

    [Fact]
    public void TheCharacterBeforeTheCaretSurvivesInterpretation()
    {
        var session = new FixedSession(new UiaRawRead
        {
            Surrounding = "Deploying Kubernetes to staging ",
            Preceding = " ",
            RoundTrips = 6,
        });
        using var worker = new UiaWorker(() => session);
        using var reader = new UiaContextReader(worker, new Blocklist(), Deadline(2_000));

        var read = reader.BeginRead(Editor).Wait();

        Assert.Equal(" ", read.Preceding);
    }

    [Fact]
    public void TheCharacterBeforeTheCaretIsKeptEvenWhenNothingIsWorthAHint()
    {
        // An almost empty box has no proper nouns in it. It still has a caret with something
        // before it, and spacing needs that whether or not the recogniser gets any terms.
        var session = new FixedSession(new UiaRawRead { Surrounding = "ok", Preceding = "k", RoundTrips = 6 });
        using var worker = new UiaWorker(() => session);
        using var reader = new UiaContextReader(worker, new Blocklist(), Deadline(2_000));

        var read = reader.BeginRead(Editor).Wait();

        Assert.Equal("k", read.Preceding);
    }

    [Fact]
    public void AReadThrownAwayForItsContentKeepsNothingAtAll()
    {
        // Discarding a read means all of it. One character is harmless, and "all of it, except"
        // is how a privacy rule stops being one.
        var session = new FixedSession(new UiaRawRead
        {
            Surrounding = "https://secure.chase.com/web/auth/dashboard",
            Preceding = "d",
            RoundTrips = 6,
        });
        using var worker = new UiaWorker(() => session);
        using var reader = new UiaContextReader(worker, new Blocklist(), Deadline(2_000));

        var read = reader.BeginRead(Editor).Wait();

        Assert.Equal(ContextOutcome.BlockedContent, read.Outcome);
        Assert.Null(read.Preceding);
    }

    /// <summary>A provider that answers at once with whatever it was given.</summary>
    private sealed class FixedSession(UiaRawRead read) : IUiaSession
    {
        public bool IsAvailable => true;

        public UiaRawRead Read(UiaReadRequest request) => read;

        public void Dispose()
        {
        }
    }

    /// <summary>A provider held open by the test, then let go.</summary>
    private sealed class BlockingSession(ManualResetEventSlim gate) : IUiaSession
    {
        public bool IsAvailable => true;

        public string? Selection { get; set; }

        public UiaRawRead Read(UiaReadRequest request)
        {
            gate.Wait();
            return new UiaRawRead { Status = UiaReadStatus.Ok, Selection = Selection, RoundTrips = 1 };
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Stands in for the COM failures the real session turns into a status.</summary>
    private sealed class COMExceptionStandIn : Exception
    {
        public COMExceptionStandIn()
            : base("The RPC server is unavailable.")
        {
        }
    }
}
