using System.Text;
using Jane.Core.Abstractions;
using Jane.Core.Modes;
using Jane.Core.Vocabulary;
using Jane.Windows.Automation;
using Jane.Windows.Injection;

namespace Jane.Windows.Tests;

/// <summary>
/// The layered selection probe, which is what makes Edit Mode safe in the applications it matters
/// most in.
/// </summary>
/// <remarks>
/// BLOCKER #8: UIA's text support is incomplete in Chromium and Electron -- Chrome, Slack, VS Code,
/// Google Docs. Without a fallback, "make it shorter" spoken over a selection there would be typed
/// into the document as those three words.
/// </remarks>
public sealed class SelectionProbeTests
{
    private static readonly TargetWindow Chrome = new(1, 2, "chrome", "Chrome_WidgetWin_1", "Docs");
    private static readonly TargetWindow Notepad = new(3, 4, "notepad", "Notepad", "Untitled");

    [Fact]
    public async Task UiaSelectionIsUsedDirectlyAndTheClipboardIsNeverTouched()
    {
        // The cheap, invisible path. Where UIA works there is no reason to send a keystroke and
        // borrow the user's clipboard.
        var clipboard = new FakeClipboard();
        var input = new RecordingSendInput();
        var probe = new SelectionProbe(clipboard, input);

        var result = await probe.ProbeAsync(Chrome, ContextReadWithSelection("The meeting is Tuesday."),
            TestContext.Current.CancellationToken);

        Assert.True(result.HasSelection);
        Assert.Equal("The meeting is Tuesday.", result.Text);
        Assert.Equal(SelectionSource.Uia, result.Source);
        Assert.Equal(0, clipboard.SetCount);
        Assert.Empty(input.Sent);
    }

    [Fact]
    public async Task ClipboardProbeReturnsASelectionWhereUiaReportsNone()
    {
        // The Chromium case. UIA says nothing; a real Ctrl+C finds the selection.
        var clipboard = new FakeClipboard { CopyProduces = "The meeting is Tuesday." };
        var input = new RecordingSendInput { OnSend = () => clipboard.SimulateCopy() };
        var probe = new SelectionProbe(clipboard, input, new SelectionProbeOptions
        {
            ClipboardProbeTimeout = TimeSpan.FromSeconds(2),
        });


        var result = await probe.ProbeAsync(Chrome, contextRead: null, TestContext.Current.CancellationToken);

        Assert.True(result.HasSelection);
        Assert.Equal("The meeting is Tuesday.", result.Text);
        Assert.Equal(SelectionSource.ClipboardProbe, result.Source);
    }

    [Fact]
    public async Task TheClipboardIsRestoredExactlyAfterTheProbe()
    {
        // A probe that lost the user's clipboard would be a bug they notice much later, in another
        // application, with no way to connect the two.
        var original = Encoding.Unicode.GetBytes("something the user copied earlier\0");
        var clipboard = new FakeClipboard { CopyProduces = "selection" };
        clipboard.Contents[ClipboardFormats.UnicodeText] = original;

        var input = new RecordingSendInput();

        var probe = new SelectionProbe(clipboard, input, new SelectionProbeOptions
        {
            ClipboardProbeTimeout = TimeSpan.FromSeconds(2),
        });

        await probe.ProbeAsync(Chrome, contextRead: null, TestContext.Current.CancellationToken);

        Assert.Equal(original, clipboard.Contents[ClipboardFormats.UnicodeText]);
    }

    [Fact]
    public async Task TheClipboardIsRestoredEvenWhenTheProbeThrowsHalfway()
    {
        var original = Encoding.Unicode.GetBytes("user data\0");
        var clipboard = new FakeClipboard { ThrowOnSet = true };
        clipboard.Contents[ClipboardFormats.UnicodeText] = original;

        var probe = new SelectionProbe(clipboard, new RecordingSendInput());

        var result = await probe.ProbeAsync(Chrome, contextRead: null, TestContext.Current.CancellationToken);

        Assert.False(result.HasSelection);
        Assert.Equal(original, clipboard.Contents[ClipboardFormats.UnicodeText]);
    }

    [Fact]
    public async Task ANonChromiumAppSkipsTheClipboardProbeEntirely()
    {
        // Notepad's UIA is complete, so "UIA saw nothing" genuinely means nothing is selected.
        // Sending a Ctrl+C here would be an invasive probe for no information.
        var clipboard = new FakeClipboard { CopyProduces = "would have been found" };
        var input = new RecordingSendInput();
        var probe = new SelectionProbe(clipboard, input);

        var result = await probe.ProbeAsync(Notepad, contextRead: null, TestContext.Current.CancellationToken);

        Assert.False(result.HasSelection);
        Assert.Empty(input.Sent);
        Assert.Equal(0, clipboard.SetCount);
    }

    [Fact]
    public async Task ATargetThatIgnoresCtrlCYieldsNoSelectionRatherThanTheSentinel()
    {
        // The sentinel exists for this case: an empty clipboard after a copy is indistinguishable
        // from "copied an empty selection", and returning the sentinel itself would be worse still
        // -- Jane would rewrite a GUID into the user's document.
        var clipboard = new FakeClipboard { CopyProduces = null };
        var probe = new SelectionProbe(clipboard, new RecordingSendInput(), new SelectionProbeOptions
        {
            ClipboardProbeTimeout = TimeSpan.FromMilliseconds(120),
        });

        var result = await probe.ProbeAsync(Chrome, contextRead: null, TestContext.Current.CancellationToken);

        Assert.False(result.HasSelection);
        Assert.DoesNotContain("jane-probe", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProbeSendsARealVirtualKeyChordRatherThanUnicode()
    {
        // A VK_PACKET cannot form a chord: modifiers and the Unicode path do not compose, so a
        // "Ctrl+C" built from Unicode packets would copy nothing at all.
        var clipboard = new FakeClipboard { CopyProduces = "text" };
        var input = new RecordingSendInput();

        var probe = new SelectionProbe(clipboard, input, new SelectionProbeOptions
        {
            ClipboardProbeTimeout = TimeSpan.FromSeconds(2),
        });

        await probe.ProbeAsync(Chrome, contextRead: null, TestContext.Current.CancellationToken);

        var sent = Assert.Single(input.Sent);
        Assert.Equal(4, sent.Length);
        Assert.All(sent, r => Assert.NotEqual(0u, r.Keyboard.VirtualKey));
    }

    [Fact]
    public async Task DisablingTheClipboardProbeLeavesOnlyUia()
    {
        var clipboard = new FakeClipboard { CopyProduces = "would have been found" };
        var input = new RecordingSendInput();
        var probe = new SelectionProbe(clipboard, input, new SelectionProbeOptions
        {
            AllowClipboardProbe = false,
        });

        var result = await probe.ProbeAsync(Chrome, contextRead: null, TestContext.Current.CancellationToken);

        Assert.False(result.HasSelection);
        Assert.Empty(input.Sent);
    }

    private static ContextRead ContextReadWithSelection(string selection) =>
        ContextRead.None with
        {
            Outcome = ContextOutcome.Read,
            Hints = ContextHints.Empty with { Selection = selection },
        };

    private sealed class RecordingSendInput : ISendInput
    {
        public List<InputRecord[]> Sent { get; } = [];

        public Action? OnSend { get; init; }

        public SendInputOutcome Send(ReadOnlySpan<InputRecord> records)
        {
            Sent.Add(records.ToArray());
            OnSend?.Invoke();
            return new SendInputOutcome((uint)records.Length, 0);
        }
    }

    /// <summary>An in-memory clipboard that can simulate a target answering, or ignoring, Ctrl+C.</summary>
    private sealed class FakeClipboard : IClipboard
    {
        public Dictionary<uint, byte[]> Contents { get; } = [];

        /// <summary>What a Ctrl+C puts on the clipboard, or null for a target that ignores it.</summary>
        public string? CopyProduces { get; init; }

        public bool ThrowOnSet { get; init; }

        public int SetCount { get; private set; }

        /// <summary>
        /// Simulates the target answering a Ctrl+C: whatever it would have copied lands on the
        /// clipboard. A null <see cref="CopyProduces"/> models an application that ignores the
        /// keystroke entirely, which is the case the sentinel exists to detect.
        /// </summary>
        public void SimulateCopy()
        {
            if (CopyProduces is not null)
            {
                Contents[ClipboardFormats.UnicodeText] = Encoding.Unicode.GetBytes(CopyProduces + '\0');
            }
        }

        public uint RegisterFormat(string name) => 0xC000;

        public IReadOnlyList<uint> GetAvailableFormats() => [.. Contents.Keys];

        public byte[]? TryGetFormatData(uint format) =>
            Contents.TryGetValue(format, out var data) ? data : null;

        public void SetContents(IReadOnlyList<ClipboardPayload> payloads)
        {
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("clipboard is locked by another application");
            }

            SetCount++;
            Contents.Clear();
            foreach (var payload in payloads)
            {
                Contents[payload.Format] = payload.Data;
            }

        }
    }
}
