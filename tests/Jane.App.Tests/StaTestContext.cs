using System.Windows.Threading;
using Xunit.Sdk;
using Xunit.v3;

// The overlay tests measure process-wide facts -- which window holds foreground, how much CPU
// this process burns at idle. Running them concurrently would have them measure each other.
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace Jane.App.Tests;

/// <summary>
/// An STA thread running a real WPF <see cref="Dispatcher"/>, for tests that drive real windows.
/// </summary>
/// <remarks>
/// xUnit v3 runs MTA, and WPF refuses to build a visual tree anywhere but an STA thread with a
/// message pump. Nothing here touches <see cref="System.Windows.Application.Current"/>: an
/// Application is process-wide singleton state that cannot be torn down, so one test creating it
/// would silently change every later test's environment. A bare dispatcher is enough --
/// PresentationBuildTasks emits an assembly-qualified pack URI (<c>/Jane;component/...</c>) into
/// <c>InitializeComponent</c>, so XAML loading does not need <c>Application.ResourceAssembly</c>.
/// </remarks>
internal sealed class StaTestContext : IDisposable
{
    private readonly Thread _thread;

    public StaTestContext(string name = "Jane STA test")
    {
        using var ready = new ManualResetEventSlim(false);
        Dispatcher? dispatcher = null;

        _thread = new Thread(() =>
        {
            // Publish the dispatcher before Run(): work queued between here and the loop
            // starting is held, not lost, so a caller can never race the pump into a deadlock.
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            Name = name,
            IsBackground = true,
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();

        Dispatcher = dispatcher!;
    }

    public Dispatcher Dispatcher { get; }

    public void Invoke(Action action) => Dispatcher.Invoke(action);

    public T Invoke<T>(Func<T> func) => Dispatcher.Invoke(func);

    /// <summary>
    /// Blocks until everything already queued -- including layout, render and the window
    /// messages a Show/Hide generates -- has run.
    /// </summary>
    public void Settle(int milliseconds = 220)
    {
        Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(milliseconds);
        Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(10));
    }
}
