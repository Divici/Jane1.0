namespace Jane.Core.Abstractions;

/// <summary>
/// Notices that the user has typed or clicked, without recording what.
/// </summary>
/// <remarks>
/// Exists for one decision. When an application will not say what sits before the caret, Jane
/// spaces a dictation off the text it last typed there -- which is only true for as long as nobody
/// has touched the window since. A key press or a click means the caret may be anywhere, so the
/// memory is discarded rather than trusted.
/// <para>
/// A counter and nothing else, on purpose: which key, which button and where are none of Jane's
/// business, and a number that only ever goes up cannot leak any of them.
/// </para>
/// </remarks>
public interface IUserActivityMonitor
{
    /// <summary>
    /// Changes whenever the user presses a key or a mouse button. Jane's own synthetic input and
    /// the dictation hotkey itself do not count.
    /// </summary>
    long Version { get; }
}

/// <summary>The default where nothing watches input: the memory is never invalidated.</summary>
public sealed class NullUserActivityMonitor : IUserActivityMonitor
{
    public static NullUserActivityMonitor Instance { get; } = new();

    public long Version => 0;
}
