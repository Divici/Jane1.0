using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jane.Core.Abstractions;

namespace Jane.App.Settings;

/// <summary>
/// A button that, once pressed, becomes the next key you press.
/// </summary>
/// <remarks>
/// <para>
/// The capture has to swallow everything while it is armed. Tab, Space, Enter and the arrow keys
/// are all legitimate hotkeys and all of them are also WPF navigation, so a picker that does not
/// mark them handled would move focus instead of binding them. Escape is the one exception: it
/// abandons the capture, because a picker you cannot get out of without binding something is a
/// trap.
/// </para>
/// <para>
/// Modifiers are read two ways on purpose. A bare modifier -- Jane's default is Right Ctrl held
/// alone -- arrives as the key itself, and must not also be reported as its own modifier or the
/// binding would read "Ctrl + Right Ctrl". Anything else takes the live modifier state.
/// </para>
/// </remarks>
public sealed class HotkeyPicker : Button
{
    public static readonly DependencyProperty BindingProperty = DependencyProperty.Register(
        nameof(Binding),
        typeof(HotkeyBinding),
        typeof(HotkeyPicker),
        new FrameworkPropertyMetadata(HotkeyBinding.Default, OnBindingChanged));

    private static readonly DependencyPropertyKey IsCapturingKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCapturing), typeof(bool), typeof(HotkeyPicker), new PropertyMetadata(false));

    public static readonly DependencyProperty IsCapturingProperty = IsCapturingKey.DependencyProperty;

    public HotkeyPicker()
    {
        Focusable = true;
        IsTabStop = true;
        UpdateCaption();
    }

    /// <summary>The key currently bound. Set by the view model; never written by the capture.</summary>
    public HotkeyBinding Binding
    {
        get => (HotkeyBinding)GetValue(BindingProperty);
        set => SetValue(BindingProperty, value);
    }

    /// <summary>True between the click and the keystroke. The caption says so.</summary>
    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        private set => SetValue(IsCapturingKey, value);
    }

    /// <summary>
    /// Raised with the captured combination.
    /// </summary>
    /// <remarks>
    /// The picker deliberately does not update <see cref="Binding"/> itself. Whether a
    /// combination is acceptable is <see cref="HotkeyValidator"/>'s decision, and a picker that
    /// showed a rejected key as bound would be lying about what Jane is listening for.
    /// </remarks>
    public event EventHandler<HotkeyBinding>? Captured;

    /// <summary>Arms the capture. Exposed so a test does not have to synthesise a click.</summary>
    public void BeginCapture()
    {
        IsCapturing = true;
        UpdateCaption();
        Focus();
    }

    public void CancelCapture()
    {
        IsCapturing = false;
        UpdateCaption();
    }

    /// <summary>
    /// Feeds one keystroke in, exactly as the live handler would.
    /// </summary>
    /// <remarks>
    /// Synthesising a real <c>KeyDown</c> through WPF's input system needs a live
    /// <c>PresentationSource</c>, which an unshown window does not have. This is the seam that
    /// keeps the capture rules testable without one.
    /// </remarks>
    public bool Capture(Key key, ModifierKeys modifiers)
    {
        if (key is Key.Escape && modifiers == ModifierKeys.None)
        {
            CancelCapture();
            return false;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var required = new List<int>();

        if (!KeyNames.IsModifier(virtualKey))
        {
            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                required.Add(KeyNames.VkControl);
            }

            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                required.Add(KeyNames.VkShift);
            }

            if (modifiers.HasFlag(ModifierKeys.Alt))
            {
                required.Add(KeyNames.VkAlt);
            }

            if (modifiers.HasFlag(ModifierKeys.Windows))
            {
                required.Add(KeyNames.VkLeftWindows);
            }
        }

        IsCapturing = false;
        UpdateCaption();
        Captured?.Invoke(this, new HotkeyBinding(virtualKey, required));
        return true;
    }

    protected override void OnClick()
    {
        base.OnClick();
        BeginCapture();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsCapturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        // Alt arrives as Key.System with the real key in SystemKey; without this, holding Alt
        // would bind "Alt + Alt".
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        e.Handled = true;
        Capture(key, Keyboard.Modifiers);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);

        // Clicking away is a cancel, not a bind. Leaving the picker armed would capture the next
        // key the user pressed in some other control entirely.
        if (IsCapturing)
        {
            CancelCapture();
        }
    }

    private static void OnBindingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyPicker)d).UpdateCaption();

    private void UpdateCaption()
    {
        Content = IsCapturing ? "Press a key..." : KeyNames.Describe(Binding);

        System.Windows.Automation.AutomationProperties.SetName(
            this,
            IsCapturing
                ? "Waiting for a key. Press the key you want to hold to dictate, or Escape to cancel."
                : $"Push-to-talk key, currently {KeyNames.Describe(Binding)}. Activate to choose a different key.");
    }
}
