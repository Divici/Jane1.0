using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Jane.App.Controls;

/// <summary>
/// The smallest change-notification base that does the job.
/// </summary>
/// <remarks>
/// Jane has no MVVM framework and does not need one. Three windows, no navigation stack, no
/// dependency container in the view layer -- a base class and two commands is the whole of it,
/// and a package would be a dependency the licence audit has to carry for no gain.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>Assigns and notifies, returning whether anything actually changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(property);
        return true;
    }
}

/// <summary>A command over a plain delegate.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A command over an asynchronous delegate, which refuses to run twice at once.
/// </summary>
/// <remarks>
/// The re-entrancy guard is the point. Every long operation in these windows is a model download
/// or a database write, and a button that starts a second 482 MB download because it was clicked
/// twice is a bug the user pays for in bandwidth.
/// </remarks>
public sealed class AsyncRelayCommand(
    Func<object?, CancellationToken, Task> execute,
    Func<object?, bool>? canExecute = null) : ICommand
{
    private CancellationTokenSource? _running;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _running is not null;

    public bool CanExecute(object? parameter) => !IsRunning && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    /// <summary>The awaitable form, so a caller (and a test) can wait for the work to finish.</summary>
    public async Task ExecuteAsync(object? parameter)
    {
        if (IsRunning)
        {
            return;
        }

        _running = new CancellationTokenSource();
        RaiseCanExecuteChanged();

        try
        {
            await execute(parameter, _running.Token);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>Cancels the run in flight, if there is one.</summary>
    public void Cancel() => _running?.Cancel();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
