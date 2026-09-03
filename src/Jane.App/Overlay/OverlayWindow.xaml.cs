using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Jane.Core.Abstractions;
using Jane.Core.Settings;

namespace Jane.App.Overlay;

/// <summary>
/// The floating status pill.
/// </summary>
/// <remarks>
/// Three independent mechanisms keep this window away from the keyboard focus, because any one
/// of them failing sends the user's dictation into the wrong place:
/// <list type="number">
/// <item><description><c>ShowActivated="False"</c> stops WPF asking for foreground on show.</description></item>
/// <item><description><c>Focusable="False"</c> keeps WPF's own focus scope out of the pill.</description></item>
/// <item><description><c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT</c>, applied in
/// <see cref="OnSourceInitialized"/>, stop Windows activating the HWND at all.</description></item>
/// </list>
/// The extended styles land at HWND creation, which the constructor forces rather than leaving
/// to the first <c>Show()</c> -- a window that exists without <c>WS_EX_NOACTIVATE</c>, even for
/// one message pump turn, is a window that can be activated.
/// </remarks>
public partial class OverlayWindow : Window
{
    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan StateCrossfade = TimeSpan.FromMilliseconds(170);

    /// <summary>
    /// How solid the pill is while something is actually happening.
    /// </summary>
    private const double ActiveOpacity = 1.0;

    /// <summary>
    /// How solid the resting pill is.
    /// </summary>
    /// <remarks>
    /// Dimmed because it is on screen permanently, over whatever the user is really doing. A
    /// resting indicator at full strength is not an indicator, it is clutter -- and the contrast
    /// between the two is itself the signal that a dictation has started.
    /// </remarks>
    private const double RestingOpacity = 0.55;

    /// <summary>~30 fps. Fast enough to read as motion, slow enough to be invisible in a profile.</summary>
    private static readonly TimeSpan WaveformInterval = TimeSpan.FromMilliseconds(33);

    private static readonly IEasingFunction Ease = CreateEase();

    private readonly DispatcherTimer _waveformPump;
    private readonly DispatcherTimer _hideTimer;

    private OverlayStatus _status = OverlayStatus.Idle;
    private OverlayAnchor _anchor = OverlayAnchor.BottomCentre;
    private float _level;
    private nint _handle;

    public OverlayWindow()
    {
        InitializeComponent();

        _waveformPump = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = WaveformInterval };
        _waveformPump.Tick += OnWaveformTick;

        // The fade-out is decorative; the window coming down is not. A timer rather than the
        // animation's Completed callback means an interrupted or never-started clock still
        // leaves an idle overlay genuinely hidden.
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = HideDuration };
        _hideTimer.Tick += OnHideElapsed;

        _ = new WindowInteropHelper(this).EnsureHandle();

        Apply(OverlayStatus.Idle);
    }

    /// <summary>
    /// The pill's text equivalent, and the window's UI Automation name.
    /// </summary>
    /// <remarks>
    /// The pill is the only feedback a screen-reader user gets during a dictation, so every state
    /// -- including Idle, which draws nothing -- carries text.
    /// </remarks>
    public string AccessibleStatusText { get; private set; } = "Jane is idle";

    public OverlayStatus Status => _status;

    /// <summary>The opacity the current state animates towards. Dimmer while resting.</summary>
    public double TargetOpacity { get; private set; } = ActiveOpacity;

    /// <summary>What the pill is drawing at right now, mid-animation included.</summary>
    public double DrawnOpacity => RootLayer.Opacity;

    /// <summary>Where the pill sits. Changing it re-places the window immediately.</summary>
    public OverlayAnchor Anchor
    {
        get => _anchor;
        set
        {
            if (_anchor == value)
            {
                return;
            }

            _anchor = value;
            QueueReposition();
        }
    }

    /// <summary>Applies a status. Must be called on this window's dispatcher.</summary>
    public void Apply(OverlayStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var previous = _status;
        var previousText = AccessibleStatusText;

        _status = status;
        AccessibleStatusText = TextFor(status);

        if (status.State == OverlayState.Idle)
        {
            BeginHide();
            return;
        }

        _level = status.Level;
        TargetOpacity = status.State == OverlayState.Ready ? RestingOpacity : ActiveOpacity;

        // A dictation in progress pushes a new audio level about thirty times a second with
        // everything else unchanged. Only the waveform pump needs those. Rebuilding the visuals,
        // re-announcing the live region and re-placing the window at that rate would be waste,
        // and re-placing a topmost window every frame is visible as a shimmer.
        var changed = previous.State != status.State
            || !string.Equals(previousText, AccessibleStatusText, StringComparison.Ordinal);

        if (changed)
        {
            UpdateVisuals(status);
        }

        if (!IsVisible)
        {
            BeginShow();
        }
        else if (changed)
        {
            // Resting and active differ in strength as well as in content, and the pill is
            // already on screen for the transition between them -- so the fade is part of the
            // state change rather than something only the show animation does.
            RootLayer.BeginAnimation(
                OpacityProperty, Animate(RootLayer.Opacity, TargetOpacity, StateCrossfade));

            // The pill resizes to its new message, and OnRenderSizeChanged re-anchors it.
            CrossfadeContent();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;
        OverlayInterop.MakeNonActivating(_handle);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(NoActivateHook);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // The pill grows and shrinks with its message, and a right-anchored window has to move
        // to stay anchored. Deferred: GetWindowRect still reports the old size this early.
        QueueReposition();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // Dragging onto a differently-scaled monitor changes the window's pixel size, so the
        // corner it was pinned to is no longer where it belongs.
        QueueReposition();
    }

    protected override void OnClosed(EventArgs e)
    {
        _waveformPump.Stop();
        _hideTimer.Stop();
        base.OnClosed(e);
    }

    private static IntPtr NoActivateHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != OverlayInterop.WmMouseActivate)
        {
            return IntPtr.Zero;
        }

        // WS_EX_TRANSPARENT already routes clicks to the window underneath, so this should never
        // arrive. It is answered anyway: the cost is one comparison per message, and the cost of
        // being wrong is a dictation typed into Jane's own pill.
        handled = true;
        return OverlayInterop.MaNoActivate;
    }

    private static IEasingFunction CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private static DoubleAnimation Animate(double from, double to, TimeSpan duration) =>
        new(from, to, new Duration(duration)) { EasingFunction = Ease };

    private static string TextFor(OverlayStatus status) =>
        string.IsNullOrWhiteSpace(status.Message) ? DefaultTextFor(status.State) : status.Message;

    private static string DefaultTextFor(OverlayState state) => state switch
    {
        // Only a fallback. The resting pill's real text names the bound hotkey, which this
        // window has no way of knowing -- IdleOverlay builds it and passes it in.
        OverlayState.Ready => "Jane is ready",
        OverlayState.Listening => "Listening",
        OverlayState.Thinking => "Thinking",
        OverlayState.Injecting => "Inserting text",
        OverlayState.EditModeUnavailable => "Edit Mode unavailable here",
        OverlayState.Error => "Something went wrong",
        _ => "Jane is idle",
    };

    private static Brush BrushFor(OverlayState state) => state switch
    {
        // The same grey the waveform uses for silence. Resting is not a state with news in it,
        // and a coloured dot sitting on screen all day reads as one.
        OverlayState.Ready => JanePalette.WaveformIdle,
        OverlayState.Listening => JanePalette.AccentBrush,
        OverlayState.Thinking => JanePalette.ThinkingBrush,
        OverlayState.Injecting => JanePalette.InjectingBrush,
        OverlayState.EditModeUnavailable => JanePalette.CautionBrush,
        OverlayState.Error => JanePalette.ErrorBrush,
        _ => JanePalette.WaveformIdle,
    };

    private void UpdateVisuals(OverlayStatus status)
    {
        StatusText.Text = AccessibleStatusText;

        var accent = BrushFor(status.State);
        StateDot.Fill = accent;
        Waveform.Fill = accent;

        var listening = status.State == OverlayState.Listening;
        Waveform.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        DotHost.Visibility = listening ? Visibility.Collapsed : Visibility.Visible;

        if (listening)
        {
            if (!_waveformPump.IsEnabled)
            {
                _waveformPump.Start();
            }
        }
        else
        {
            _waveformPump.Stop();
            Waveform.Reset();
        }

        PulseDot(status.State == OverlayState.Thinking);

        AutomationProperties.SetName(this, AccessibleStatusText);
        AutomationProperties.SetName(Pill, AccessibleStatusText);

        // Resting is deliberately not announced. Jane returns to it after every single
        // dictation, and a screen reader saying "Hold Right Ctrl to dictate" each time somebody
        // finishes speaking would be unusable. It is still on the automation tree to be read on
        // request; it just does not interrupt.
        if (status.State != OverlayState.Ready)
        {
            AnnounceLiveRegion();
        }
    }

    private void PulseDot(bool pulse)
    {
        if (!pulse)
        {
            StateDot.BeginAnimation(OpacityProperty, null);
            StateDot.Opacity = 1;
            return;
        }

        // Transcribing and formatting have no measurable progress to show, so the pill breathes
        // rather than lying with a progress bar.
        StateDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void BeginShow()
    {
        _hideTimer.Stop();
        RootLayer.BeginAnimation(OpacityProperty, null);
        RootShift.BeginAnimation(TranslateTransform.YProperty, null);
        RootLayer.Opacity = 0;
        RootShift.Y = 6;

        Show();

        // Position after the first layout pass. SizeToContent has produced no measured size yet,
        // so placing now would anchor the previous dictation's width against the corner.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_status.State == OverlayState.Idle)
            {
                return;
            }

            Reposition();
            RootLayer.BeginAnimation(OpacityProperty, Animate(0, TargetOpacity, ShowDuration));
            RootShift.BeginAnimation(TranslateTransform.YProperty, Animate(6, 0, ShowDuration));
        });
    }

    private void BeginHide()
    {
        _waveformPump.Stop();
        _level = 0;
        Waveform.Reset();
        PulseDot(false);

        if (!IsVisible)
        {
            RootLayer.Opacity = 0;
            return;
        }

        RootLayer.BeginAnimation(OpacityProperty, Animate(RootLayer.Opacity, 0, HideDuration));
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnHideElapsed(object? sender, EventArgs e)
    {
        _hideTimer.Stop();

        if (_status.State == OverlayState.Idle)
        {
            Hide();
        }
    }

    private void CrossfadeContent() =>
        PillContent.BeginAnimation(OpacityProperty, Animate(0.25, 1, StateCrossfade));

    private void OnWaveformTick(object? sender, EventArgs e)
    {
        Waveform.Push(_level);

        // A level that stops arriving should fall away rather than freeze mid-wave, so a stalled
        // audio path looks like silence instead of like a live microphone.
        _level *= 0.94f;
    }

    private void QueueReposition()
    {
        if (IsVisible)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Reposition);
        }
    }

    private void Reposition()
    {
        if (_handle == 0 || !OverlayInterop.TryResolveTrayAnchor(out var workArea, out var edge))
        {
            return;
        }

        var rect = OverlayInterop.WindowRect(_handle);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var margin = (int)Math.Round(OverlayPlacement.MarginDip * OverlayInterop.DpiScale(_handle));

        var placement = _anchor == OverlayAnchor.BottomCentre
            ? OverlayPlacement.BottomCentre(workArea, rect.Width, rect.Height, margin)
            : OverlayPlacement.Calculate(workArea, edge, rect.Width, rect.Height, margin);

        OverlayInterop.MoveWithoutActivating(_handle, placement);
    }

    private void AnnounceLiveRegion()
    {
        // Creating a peer with no client listening allocates an automation tree nobody reads.
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        var peer = UIElementAutomationPeer.FromElement(StatusText)
            ?? UIElementAutomationPeer.CreatePeerForElement(StatusText);

        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
