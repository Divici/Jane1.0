namespace Jane.Core.Abstractions;

/// <summary>How the hotkey drives a dictation.</summary>
public enum HotkeyMode
{
    /// <summary>Hold to talk. Release ends the dictation. Aqua's only documented mode.</summary>
    Hold,

    /// <summary>Press to start, press again to stop. Auto-stops at a maximum duration.</summary>
    Toggle,
}

/// <summary>
/// A key Jane listens for, in the form the low-level hook reports.
/// </summary>
/// <remarks>
/// The default -- Right Ctrl held alone -- is a bare modifier. <c>RegisterHotKey</c> cannot
/// express that: it needs at least one modifier bit plus a real key, and fires once on key-down
/// rather than reporting a held state. That is why Jane installs a <c>WH_KEYBOARD_LL</c> hook.
/// </remarks>
/// <param name="VirtualKey">A Win32 VK_ code. VK_RCONTROL (0xA3) by default.</param>
/// <param name="RequiresModifiers">
/// Modifier bits that must also be down. Empty for the bare-modifier default.
/// </param>
public sealed record HotkeyBinding(int VirtualKey, IReadOnlyList<int> RequiresModifiers)
{
    /// <summary>
    /// VK_RCONTROL. Chosen at onboarding with live conflict detection, and flagged there as a
    /// common game bind -- push-to-talk in voice chat uses it constantly.
    /// </summary>
    public const int VkRightControl = 0xA3;

    public const int VkEscape = 0x1B;

    public static HotkeyBinding Default { get; } = new(VkRightControl, []);
}

/// <param name="Duration">
/// Measured from key-down to key-up. Holds under the configured minimum cancel silently, which
/// is what stops a game bind or a stray brush of the key from firing a dictation.
/// </param>
public sealed record HotkeyEvent(HotkeyEventKind Kind, TimeSpan Duration, DateTimeOffset Timestamp);

public enum HotkeyEventKind
{
    /// <summary>Key-down. The capture arms and Deep Context starts, but no model work begins.</summary>
    Pressed,

    /// <summary>Key-up after at least the minimum hold. Run the pipeline.</summary>
    Released,

    /// <summary>Key-up under the minimum hold. Discard the buffer, show nothing.</summary>
    TooShort,

    /// <summary>Esc while listening or transcribing. Consumed; otherwise Esc passes through.</summary>
    Cancelled,
}

/// <summary>
/// A global keyboard hook that reports hold-to-talk state.
/// </summary>
/// <remarks>
/// The hook procedure itself must enqueue and return -- no allocation, no I/O, no locks. Windows
/// silently removes a hook whose callback exceeds <c>LowLevelHooksTimeout</c>, at which point the
/// hotkey stops working with no error anywhere, so a watchdog re-installs it.
/// </remarks>
public interface IHotkeyListener : IDisposable
{
    HotkeyBinding Binding { get; }

    HotkeyMode Mode { get; }

    /// <summary>True while the bound key is physically down.</summary>
    bool IsHeld { get; }

    event EventHandler<HotkeyEvent>? HotkeyEvent;

    void Start();

    void Rebind(HotkeyBinding binding, HotkeyMode mode);

    /// <summary>
    /// Swaps the hold thresholds on a listener that is already installed.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Rebind"/> because these are the only two settings that change the
    /// meaning of a key press without changing which key it is, and because a rebind abandons any
    /// dictation in flight while a threshold change has no reason to.
    /// </remarks>
    void Reconfigure(TimeSpan minimumHold, TimeSpan maxDuration);
}
