using System.Diagnostics;
using System.Runtime.InteropServices;
using Jane.Core.Abstractions;
using Jane.Windows.Automation;
using Jane.Windows.Injection;

namespace Jane.Windows.Tests;

/// <summary>
/// Injection into the real Windows 11 Notepad, read back out of it.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that was missing, and its absence is why Notepad was broken for two releases
/// running. Every other Notepad assertion in this repository is made against a classic Win32
/// <c>EDIT</c> control while passing a target descriptor that merely claims to be Notepad -- and
/// the real Notepad has not been a classic edit control since Windows 11. So the suite verified a
/// target that behaves nothing like the one the user dictates into, and reported success while the
/// text arrived truncated.
/// </para>
/// <para>
/// What it costs: a Notepad window opens, receives a sentence, is emptied and closed. It takes the
/// foreground for a few seconds and skips when it cannot get it, in keeping with
/// <c>ContextReaderTests</c>. That cost is worth paying for the one check that can see this class
/// of bug, because <c>SendInput</c> reports full success while the characters are being dropped.
/// </para>
/// </remarks>
[Trait("Category", "RealDevice")]
public sealed class RealNotepadTests
{
    private const string Sentence = "Quick test to see how it's improved.";

    private const uint WmClose = 0x0010;

    [Fact]
    public async Task ADictationArrivesWholeInRealNotepad()
    {
        Assert.SkipWhen(AnyNotepadIsOpen(), AlreadyOpen);

        // There is one foreground window per desktop and several test projects want it. Without
        // this, launching Notepad here takes the foreground from the governor's borderless-window
        // tests and the caret locator, and all three fail for reasons unrelated to their subject.
        using var foreground = ForegroundLock.Acquire();

        using var notepad = Launch();
        var target = await WaitForForegroundNotepadAsync();
        Assert.SkipWhen(target is null, "Notepad did not take the foreground in this session.");

        var focus = new FocusedAppIdentity();
        var sendInput = new Win32SendInput();
        var injector = new RoutingTextInjector(
            new InjectionStrategySelector(),
            new SendInputInjector(focus, focus, sendInput, new ModifierGate(new Win32AsyncKeyState())),
            new ClipboardInjector(
                focus, focus, new Win32Clipboard(), sendInput, new ModifierGate(new Win32AsyncKeyState())));

        try
        {
            var result = await injector.InjectAsync(Sentence, target!, TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.Detail);

            // Notepad's text host applies the input on its own schedule, so the read cannot follow
            // the call immediately.
            await Task.Delay(1_200, TestContext.Current.CancellationToken);
            var arrived = await ReadBackAsync(target!);

            // The whole sentence, not a prefix of it. SendInput reported success for both the
            // truncated and the whole delivery, which is exactly why this reads the document.
            Assert.Equal(Sentence, arrived?.Trim());
        }
        finally
        {
            Clear(sendInput);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            PostMessageW(target!.Handle, WmClose, 0, 0);
        }
    }

    [Fact]
    public async Task TheRoutingRuleSendsRealNotepadToTheClipboard()
    {
        // Pins the decision against the real window's own process and class, rather than against
        // a descriptor a test wrote down. A Notepad that reported a different process name would
        // silently fall back to the keystroke path that loses characters.
        Assert.SkipWhen(AnyNotepadIsOpen(), AlreadyOpen);

        using var foreground = ForegroundLock.Acquire();
        using var notepad = Launch();
        var target = await WaitForForegroundNotepadAsync();
        Assert.SkipWhen(target is null, "Notepad did not take the foreground in this session.");

        var decision = new InjectionStrategySelector().Select(target!, Sentence.Length);

        try
        {
            Assert.Equal(InjectionStrategy.Clipboard, decision.Strategy);
        }
        finally
        {
            PostMessageW(target!.Handle, WmClose, 0, 0);
        }
    }

    private const string AlreadyOpen =
        "A Notepad window is already open. This test types into the window it launches and closes it afterwards, so it will not go near one that is already on screen -- it may hold work that has not been saved. Close Notepad and run again.";

    /// <summary>
    /// Whether any Notepad is running, in which case this test must not run at all.
    /// </summary>
    /// <remarks>
    /// Two reasons, and either alone would be enough. Windows 11's Notepad opens a tab in an
    /// existing instance rather than a new window, so a launch would not get the foreground and
    /// the keystrokes would land in whatever document the user already had open. And a test has no
    /// business sending Ctrl+A and Delete to a window it did not create.
    /// </remarks>
    private static bool AnyNotepadIsOpen() =>
        Process.GetProcessesByName("Notepad").Length > 0;

    /// <remarks>
    /// Windows 11's <c>notepad.exe</c> hands off to the Store app, so the process this starts
    /// exits and its <c>MainWindowHandle</c> is never valid. The window is found through the
    /// foreground instead, which works because a sole new instance takes the foreground itself.
    /// </remarks>
    private static Process? Launch() =>
        Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });

    /// <summary>
    /// Finds Notepad's window and puts it in front, or gives up so the test can skip.
    /// </summary>
    /// <remarks>
    /// Launching is not enough. Windows 11's Notepad reuses an already-running instance and opens
    /// a tab in it, which does not raise the window, so waiting for it to become foreground on its
    /// own waits forever whenever a Notepad is already open. The window is located by class and
    /// raised explicitly instead.
    /// </remarks>
    private static async Task<TargetWindow?> WaitForForegroundNotepadAsync()
    {
        var focus = new FocusedAppIdentity();
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            var window = FindWindowW("Notepad", null);
            if (window != 0)
            {
                ShowWindow(window, SwShowNormal);
                SetForegroundWindow(window);
                await Task.Delay(400, TestContext.Current.CancellationToken);

                var current = focus.GetForegroundWindow();
                if (current.ProcessName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
                {
                    // The text host is not ready the instant the window is.
                    await Task.Delay(400, TestContext.Current.CancellationToken);
                    return current;
                }
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        return null;
    }

    /// <summary>Ctrl+A then Delete, so the window closes without offering to save.</summary>
    private static void Clear(Win32SendInput sendInput)
    {
        const int a = 0x41;
        const int delete = 0x2E;

        Span<InputRecord> records =
        [
            InputRecord.VirtualKey(VirtualKeys.Control, keyUp: false),
            InputRecord.VirtualKey(a, keyUp: false),
            InputRecord.VirtualKey(a, keyUp: true),
            InputRecord.VirtualKey(VirtualKeys.Control, keyUp: true),
            InputRecord.VirtualKey(delete, keyUp: false),
            InputRecord.VirtualKey(delete, keyUp: true),
        ];

        sendInput.Send(records);
    }

    /// <summary>
    /// Reads what the document actually holds, through UI Automation.
    /// </summary>
    /// <remarks>
    /// <c>WM_GETTEXT</c> cannot do this: the text area has no classic child control to send it to.
    /// UIA is the mechanism that crosses the app-model boundary, and it is what the context reader
    /// already uses against Chrome.
    /// </remarks>
    private static async Task<string?> ReadBackAsync(TargetWindow target)
    {
        using var worker = UiaWorker.CreateDefault();

        if (!worker.TrySubmit(new UiaReadRequest(target) { SoftBudget = TimeSpan.FromSeconds(3) }, out var completion))
        {
            return null;
        }

        var raw = await completion.WaitAsync(TimeSpan.FromSeconds(8));
        return raw.Value ?? raw.Surrounding ?? raw.Selection;
    }

    private const int SwShowNormal = 1;

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
