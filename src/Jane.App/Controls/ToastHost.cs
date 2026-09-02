using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Jane.App.Controls;

/// <param name="Detail">The second line: what to do about it. Null for a bare confirmation.</param>
public sealed record ToastMessage(string Text, Severity Severity = Severity.Info, string? Detail = null)
{
    /// <summary>What a screen reader announces, and what the tests assert against.</summary>
    public string Announcement => Detail is null ? Text : $"{Text} {Detail}";
}

/// <summary>
/// Transient messages, stacked in the corner of a window.
/// </summary>
/// <remarks>
/// <para>
/// Jane already owns a floating pill, and it is emphatically not for this: the pill reports the
/// dictation the user is in the middle of, and it is deliberately unfocusable and unclickable.
/// A re-inject that refused because the target window closed belongs to the history window that
/// was asked for it.
/// </para>
/// <para>
/// The live region is on the item, not the host, so a screen reader announces the message rather
/// than "list, one item". Dismissal is on a timer <em>and</em> a button, because a message a
/// screen-reader user has not reached yet must not disappear on a timer they never saw.
/// </para>
/// </remarks>
public sealed class ToastHost : Control
{
    private readonly ObservableCollection<ToastMessage> _messages = [];
    private readonly List<DispatcherTimer> _timers = [];

    public ToastHost()
    {
        Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);
        DismissCommand = new RelayCommand(parameter =>
        {
            if (parameter is ToastMessage message)
            {
                Dismiss(message);
            }
        });
    }

    /// <summary>Oldest first, so a new message appears below the one it follows.</summary>
    public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

    /// <summary>Bound to each message's close button, so dismissal is not only on a timer.</summary>
    public RelayCommand DismissCommand { get; }

    /// <summary>The most recent message, or null if none is showing.</summary>
    public ToastMessage? Latest => _messages.Count == 0 ? null : _messages[^1];

    /// <summary>How long a message stays before it dismisses itself.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Everything shown since the host was created, including dismissed messages.</summary>
    /// <remarks>The window's own record, so a test can assert what the user was told.</remarks>
    public IReadOnlyList<ToastMessage> History => _history;

    private readonly List<ToastMessage> _history = [];

    public void Show(string text, Severity severity = Severity.Info, string? detail = null) =>
        Show(new ToastMessage(text, severity, detail));

    public void Show(ToastMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _messages.Add(message);
        _history.Add(message);

        // Three at a time. Past that the oldest is certainly stale, and a column of toasts
        // covering the list they are about helps nobody.
        while (_messages.Count > 3)
        {
            _messages.RemoveAt(0);
        }

        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = Duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _timers.Remove(timer);
            Dismiss(message);
        };

        _timers.Add(timer);
        timer.Start();
    }

    public void Dismiss(ToastMessage message) => _messages.Remove(message);

    public void Clear()
    {
        foreach (var timer in _timers)
        {
            timer.Stop();
        }

        _timers.Clear();
        _messages.Clear();
    }
}
