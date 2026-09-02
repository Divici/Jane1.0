namespace Jane.Windows.Hotkeys;

/// <summary>Everything about the hotkey that a user can change, plus the two internal cadences.</summary>
public sealed record HotkeyOptions
{
    /// <summary>
    /// Holds shorter than this cancel silently -- no overlay, no model work, no text.
    /// </summary>
    /// <remarks>
    /// Right Ctrl sits under the little finger and is a common push-to-talk bind in games and
    /// voice chat, so a brush of the key has to cost nothing at all.
    /// </remarks>
    public TimeSpan MinimumHold { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// A dictation is force-ended after this long and proceeds through the pipeline.
    /// </summary>
    /// <remarks>
    /// Toggle mode makes it easy to leave dictation running by accident, and a key-up lost to a
    /// UAC prompt or a session switch does the same to hold mode.
    /// </remarks>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the pump drains the hook's queue.
    /// </summary>
    /// <remarks>
    /// The hook proc cannot signal an event without a kernel transition, so the pump polls. At
    /// 5 ms that is 200 near-empty wakeups a second -- invisible against the idle budget, and
    /// far inside the 500 ms of pre-roll that covers any delay it adds.
    /// </remarks>
    public TimeSpan PumpInterval { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>How often <see cref="HookWatchdog"/> checks that the hook is still installed.</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The budget a low-level hook callback has before Windows removes the hook without telling
    /// anyone. Mirrors <c>HKCU\Control Panel\Desktop\LowLevelHooksTimeout</c>, whose default is
    /// 5000 ms; a callback measured beyond it is treated as "the hook is gone" and re-installed.
    /// </summary>
    public TimeSpan LowLevelHooksTimeout { get; init; } = TimeSpan.FromMilliseconds(5_000);
}
