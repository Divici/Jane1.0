using System.Diagnostics;
using Jane.Core.Abstractions;

namespace Jane.Windows.Hotkeys;

/// <summary>
/// Turns raw key transitions into the four events the pipeline understands.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="LowLevelKeyboardHook"/> and free of Win32: every rule
/// that decides whether a dictation happens -- minimum hold, auto-repeat, toggle, Esc, the
/// duration cap, required modifiers -- lives here where it can be driven with synthetic events
/// and asserted, rather than inside a callback that only fires on a real key press.
/// <para>
/// Runs on the pump thread only. It is free to allocate; the hook proc is not.
/// </para>
/// </remarks>
public sealed class HotkeyStateMachine
{
    private readonly HotkeyOptions _options;

    // Physical key state, rebuilt from the hook's own stream rather than polled from
    // GetAsyncKeyState -- polling would mean a syscall per event, and the hook already sees
    // every transition. Keys held before Jane started are simply unknown until they move.
    private readonly HashSet<int> _keysDown = [];

    private long _pressedAt;

    public HotkeyStateMachine(HotkeyBinding binding, HotkeyMode mode, HotkeyOptions? options = null)
    {
        Binding = binding;
        Mode = mode;
        _options = options ?? new HotkeyOptions();
    }

    public HotkeyBinding Binding { get; private set; }

    public HotkeyMode Mode { get; private set; }

    /// <summary>True while the bound key is physically down.</summary>
    public bool IsHeld { get; private set; }

    /// <summary>True between <see cref="HotkeyEventKind.Pressed"/> and whatever ends it.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Swaps the binding. Any dictation in flight is abandoned rather than transferred.</summary>
    public void Rebind(HotkeyBinding binding, HotkeyMode mode)
    {
        Binding = binding;
        Mode = mode;
        IsHeld = false;
        IsActive = false;
    }

    /// <summary>Consumes one key transition, emitting zero or one hotkey event.</summary>
    public void Handle(in RawKeyEvent keyEvent, Action<HotkeyEvent> emit)
    {
        if (keyEvent.IsDown)
        {
            _keysDown.Add(keyEvent.VirtualKey);
        }
        else
        {
            _keysDown.Remove(keyEvent.VirtualKey);
        }

        if (keyEvent.VirtualKey == HotkeyBinding.VkEscape)
        {
            if (keyEvent.IsDown && IsActive)
            {
                IsActive = false;

                // IsHeld is deliberately left alone: the hotkey is still physically down, and
                // the key-up that eventually arrives must not then run the pipeline.
                emit(Event(HotkeyEventKind.Cancelled, Since(keyEvent.Timestamp)));
            }

            return;
        }

        if (keyEvent.VirtualKey != Binding.VirtualKey)
        {
            return;
        }

        if (keyEvent.IsDown)
        {
            OnBoundKeyDown(keyEvent, emit);
        }
        else
        {
            OnBoundKeyUp(keyEvent, emit);
        }
    }

    /// <summary>
    /// Drives the duration cap. Called by the pump on every sweep, so a dictation ends even when
    /// no further key event ever arrives.
    /// </summary>
    public void Tick(long timestamp, Action<HotkeyEvent> emit)
    {
        if (!IsActive)
        {
            return;
        }

        var elapsed = Since(timestamp);
        if (elapsed < _options.MaxDuration)
        {
            return;
        }

        // Released rather than Cancelled: the plan says a capped dictation proceeds through the
        // pipeline, so the user gets the words they already said.
        IsActive = false;
        emit(Event(HotkeyEventKind.Released, elapsed));
    }

    private void OnBoundKeyDown(in RawKeyEvent keyEvent, Action<HotkeyEvent> emit)
    {
        // Windows repeats WM_KEYDOWN while a key is held. Treating a repeat as a fresh press
        // would restart the hold clock and turn every dictation into a sub-minimum cancel.
        if (IsHeld || !ModifiersSatisfied())
        {
            return;
        }

        IsHeld = true;

        if (Mode == HotkeyMode.Toggle && IsActive)
        {
            var held = Since(keyEvent.Timestamp);
            IsActive = false;
            emit(Event(Verdict(held), held));
            return;
        }

        _pressedAt = keyEvent.Timestamp;
        IsActive = true;
        emit(Event(HotkeyEventKind.Pressed, TimeSpan.Zero));
    }

    private void OnBoundKeyUp(in RawKeyEvent keyEvent, Action<HotkeyEvent> emit)
    {
        IsHeld = false;

        // In toggle mode the key-up that follows the starting press means nothing, and a stale
        // IsActive from a cancel or a duration cap must not produce a second verdict.
        if (Mode != HotkeyMode.Hold || !IsActive)
        {
            return;
        }

        var held = Since(keyEvent.Timestamp);
        IsActive = false;
        emit(Event(Verdict(held), held));
    }

    private HotkeyEventKind Verdict(TimeSpan held) =>
        held < _options.MinimumHold ? HotkeyEventKind.TooShort : HotkeyEventKind.Released;

    private bool ModifiersSatisfied()
    {
        for (var i = 0; i < Binding.RequiresModifiers.Count; i++)
        {
            if (!_keysDown.Contains(Binding.RequiresModifiers[i]))
            {
                return false;
            }
        }

        return true;
    }

    private TimeSpan Since(long timestamp) => Stopwatch.GetElapsedTime(_pressedAt, timestamp);

    private static HotkeyEvent Event(HotkeyEventKind kind, TimeSpan duration) =>
        new(kind, duration, DateTimeOffset.UtcNow);
}
