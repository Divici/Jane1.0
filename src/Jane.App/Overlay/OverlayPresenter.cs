using System.Windows.Threading;
using Jane.Core.Abstractions;

namespace Jane.App.Overlay;

/// <summary>
/// The thread-safe face of the overlay: <see cref="IOverlayPresenter"/> over a real WPF window.
/// </summary>
/// <remarks>
/// Callers are the audio thread, the hotkey hook's queue drainer and the pipeline -- none of
/// which are the UI thread, and none of which should ever block on it. Updates are posted, not
/// invoked: a listening dictation pushes an audio level roughly thirty times a second, and a
/// synchronous hop per push would put UI latency inside the capture loop.
/// <para>
/// <see cref="Status"/> is answered from the caller's own thread rather than the window's, so
/// reading it never needs the dispatcher either.
/// </para>
/// </remarks>
public sealed class OverlayPresenter : IOverlayPresenter, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private OverlayStatus _status = OverlayStatus.Idle;
    private bool _disposed;

    public OverlayPresenter(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _dispatcher = dispatcher;
        Window = dispatcher.Invoke(static () => new OverlayWindow());
    }

    /// <summary>
    /// The window itself, exposed so Phase 8 can re-anchor it against a caret rectangle without
    /// the presenter having to grow a geometry API it does not otherwise need.
    /// </summary>
    public OverlayWindow Window { get; }

    public OverlayStatus Status => Volatile.Read(ref _status);

    public void Show(OverlayStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        Volatile.Write(ref _status, status);
        Post(() => Window.Apply(status));
    }

    public void Hide() => Show(OverlayStatus.Idle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.Invoke(Window.Close);
        }
    }

    private void Post(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = _dispatcher.BeginInvoke(action);
    }
}
