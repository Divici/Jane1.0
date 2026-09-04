using Jane.Core.Abstractions;
using Jane.Core.Settings;
using Jane.Core.Storage;

namespace Jane.App.Composition;

/// <summary>
/// Pushes a changed <see cref="JaneSettings"/> into the parts of a running Jane that hold their
/// own copy of it.
/// </summary>
/// <remarks>
/// <para>
/// The settings window has no Save button; every setter writes immediately. That is the right
/// design and it hides a trap, which Jane fell into: a value written to SQLite changes nothing
/// on its own. The keyboard hook was constructed with a binding at startup and kept it forever,
/// so rebinding the hotkey wrote a row that nothing read and the old key stayed live. The tray's
/// mode item had a bespoke wire of its own; nothing else did.
/// </para>
/// <para>
/// So there is one wire, here, driven by <see cref="SettingsRepository.Changed"/> -- which
/// already fired on every write and had no subscribers. Anything that needs telling about a
/// setting goes in <see cref="Apply"/>, and the alternative -- a callback per setting, threaded
/// from the window through the host to the object that cares -- is how the bug happened.
/// </para>
/// <para>
/// Settings that genuinely cannot be applied to a running process are not silently ignored:
/// they are listed in <see cref="RequiresRestart"/> so the window can say so.
/// </para>
/// </remarks>
public sealed class LiveSettings : IDisposable
{
    private readonly IHotkeyListener _hotkeys;
    private readonly IAudioSource _microphone;
    private readonly Action<OverlaySettings> _overlay;
    private readonly Action<TextSettings> _text;

    private SettingsRepository? _repository;
    private bool _disposed;

    public LiveSettings(
        IHotkeyListener hotkeys,
        IAudioSource microphone,
        Action<OverlaySettings> overlay,
        Action<TextSettings> text)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(microphone);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(text);

        _hotkeys = hotkeys;
        _microphone = microphone;
        _overlay = overlay;
        _text = text;
    }

    /// <summary>
    /// The settings a running Jane cannot adopt, and why.
    /// </summary>
    /// <remarks>
    /// Both need a model loaded into memory to be swapped for a different one, which is a
    /// restart's worth of work and several seconds of it. Named here rather than left to the
    /// user to discover, because a setting that quietly does nothing is the defect this whole
    /// class exists to fix.
    /// </remarks>
    public static IReadOnlyList<string> RequiresRestart { get; } =
    [
        "the speech engine and its thread count",
        "the LLM models and their context size",
    ];

    /// <summary>Applies settings now, and on every subsequent write to the repository.</summary>
    public void Attach(SettingsRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _repository = repository;
        repository.Changed += OnChanged;
        Apply(repository.Current);
    }

    public void Apply(JaneSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var hotkey = settings.Hotkey;
        var binding = hotkey.ToBinding();

        // Only when it actually changed, and compared structurally. A rebind abandons any hold in
        // flight, and every setter in the settings window writes the whole object -- so a
        // reference comparison here would have adjusting the LLM's context size silently kill a
        // dictation somebody was in the middle of.
        if (!SameBinding(binding, _hotkeys.Binding) || hotkey.Mode != _hotkeys.Mode)
        {
            _hotkeys.Rebind(binding, hotkey.Mode);
        }

        _hotkeys.Reconfigure(
            TimeSpan.FromMilliseconds(hotkey.MinimumHoldMs),
            TimeSpan.FromMilliseconds(hotkey.MaxToggleDurationMs));

        _microphone.Reconfigure(settings.Routing);
        _overlay(settings.Overlay);
        _text(settings.Text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_repository is not null)
        {
            _repository.Changed -= OnChanged;
            _repository = null;
        }
    }

    /// <summary>
    /// Structural comparison. <see cref="HotkeyBinding"/> is a record whose modifier list is an
    /// <c>IReadOnlyList</c>, so its generated equality compares that list by reference -- two
    /// bindings on the same key with the same empty modifier list are not equal to each other.
    /// </summary>
    private static bool SameBinding(HotkeyBinding left, HotkeyBinding right) =>
        left.VirtualKey == right.VirtualKey &&
        left.RequiresModifiers.SequenceEqual(right.RequiresModifiers);

    private void OnChanged(object? sender, JaneSettings settings)
    {
        if (!_disposed)
        {
            Apply(settings);
        }
    }
}
