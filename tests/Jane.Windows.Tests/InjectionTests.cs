using System.Runtime.InteropServices;
using System.Text;
using Jane.Core.Abstractions;
using Jane.Windows.Automation;
using Jane.Windows.Injection;

namespace Jane.Windows.Tests;

/// <summary>
/// Text injection, asserted against the decoded <c>INPUT</c> stream rather than a real app.
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs headless. No Notepad is launched, no keystroke escapes into the
/// session, and the developer's real clipboard is never touched -- the Win32 calls sit behind
/// <see cref="ISendInput"/>, <see cref="IClipboard"/> and <see cref="IAsyncKeyState"/>, and the
/// fakes record what would have gone out.
/// </para>
/// <para>
/// Reconstructing the string from the recorded <c>KEYEVENTF_UNICODE</c> records is a stronger
/// check than reading it back out of Notepad: it proves the exact UTF-16 code units Windows
/// would have been handed, including both halves of every surrogate pair, which a Notepad
/// round-trip would silently normalise away.
/// </para>
/// </remarks>
[Trait("Category", "Injection")]
public sealed class InjectionTests
{
    private static readonly ModifierGateOptions FastGate =
        new(Timeout: TimeSpan.FromMilliseconds(200), PollInterval: TimeSpan.FromMilliseconds(1));

    private static readonly ClipboardInjectorOptions NoSettle =
        ClipboardInjectorOptions.Default with { PasteSettleDelay = TimeSpan.Zero };

    private static readonly TargetWindow Notepad =
        new(Handle: 0x00BEEF, ProcessId: 4242, ProcessName: "notepad", WindowClass: "Notepad", WindowTitle: "Untitled");

    // ---------------------------------------------------------------- SendInput: the text itself

    [Fact]
    public async Task FiveHundredCharactersWithEmojiArriveByteIdentical()
    {
        var text = LongTextWithEmoji();
        var send = new FakeSendInput();
        var injector = NewSendInputInjector(send, out _);

        var result = await injector.InjectAsync(text, Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(InjectionStrategy.Unicode, result.Strategy);
        Assert.Equal(text.Length, result.CharactersSent);
        Assert.Equal(text, send.DecodeText());
        Assert.True(text.Length >= 500);
        Assert.Contains("\U0001F600", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SurrogatePairsGoOutAsTwoUtf16CodeUnitsInOrder()
    {
        // The failure this guards against is an injector that casts a rune to char, or that
        // chunks between the two halves. Both mangle the emoji into replacement characters.
        var send = new FakeSendInput();
        var injector = NewSendInputInjector(send, out _);

        await injector.InjectAsync("\U0001F600", Notepad, TestContext.Current.CancellationToken);

        var records = send.AllRecords;
        Assert.Equal(4, records.Count);
        Assert.Equal(0xD83D, records[0].Keyboard.ScanCode);
        Assert.Equal(0xD83D, records[1].Keyboard.ScanCode);
        Assert.Equal(0xDE00, records[2].Keyboard.ScanCode);
        Assert.Equal(0xDE00, records[3].Keyboard.ScanCode);
        Assert.All(records, r => Assert.Equal(0, r.Keyboard.VirtualKey));
        Assert.All(records, r => Assert.True((r.Keyboard.Flags & KeyEventFlags.Unicode) != 0));
        Assert.Equal(0u, records[0].Keyboard.Flags & KeyEventFlags.KeyUp);
        Assert.NotEqual(0u, records[1].Keyboard.Flags & KeyEventFlags.KeyUp);
    }

    [Fact]
    public async Task TextIsChunkedSoNoSingleBatchExceedsTheSafeSize()
    {
        // OpenWhispr#829: a single oversized synthetic batch fails silently -- no error, no
        // text. Chunking is the mitigation, and a surrogate pair must never straddle a batch.
        var text = LongTextWithEmoji();
        var send = new FakeSendInput();
        var options = SendInputInjectorOptions.Default;
        var injector = NewSendInputInjector(send, out _, options);

        await injector.InjectAsync(text, Notepad, TestContext.Current.CancellationToken);

        Assert.True(send.Batches.Count > 1, "500 characters must not go out as one batch");
        Assert.All(send.Batches, batch => Assert.InRange(batch.Length, 1, options.MaxRecordsPerBatch));
        Assert.True(options.MaxRecordsPerBatch <= 200, "the safe size must stay under the observed failure point");
        Assert.All(send.Batches, batch => Assert.False(SplitsASurrogatePair(batch)));
        Assert.Equal(text, send.DecodeText());
    }

    [Fact]
    public async Task NewlinesBecomeEnterKeystrokesBecauseUnicodeLineFeedDoesNotSubmitALine()
    {
        // KEYEVENTF_UNICODE with U+000A produces a bare line feed that most Win32 edit controls
        // ignore. The line break the user dictated has to arrive as a real VK_RETURN.
        var send = new FakeSendInput();
        var injector = NewSendInputInjector(send, out _);

        var result = await injector.InjectAsync("a\r\nb\nc", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("a\nb\nc", send.DecodeText());
        Assert.Equal(2, send.AllRecords.Count(r =>
            r.Keyboard.VirtualKey == VirtualKeys.Return && (r.Keyboard.Flags & KeyEventFlags.KeyUp) == 0));
        Assert.Equal(4, send.AllRecords.Count(r => r.Keyboard.VirtualKey == VirtualKeys.Return));
        Assert.DoesNotContain(send.AllRecords, r => r.Keyboard.ScanCode is '\r' or '\n');
    }

    [Fact]
    public async Task EveryRecordIsAKeyboardRecord()
    {
        var send = new FakeSendInput();
        var injector = NewSendInputInjector(send, out _);

        await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.All(send.AllRecords, r => Assert.Equal(InputRecord.TypeKeyboard, r.Type));
    }

    [Fact]
    public async Task EmptyTextSucceedsWithoutTouchingTheKeyboard()
    {
        var send = new FakeSendInput();
        var injector = NewSendInputInjector(send, out _);

        var result = await injector.InjectAsync("", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.CharactersSent);
        Assert.Empty(send.AllRecords);
    }

    // ---------------------------------------------------------------- SendInput: the modifier gate

    [Fact]
    public async Task InjectionWhileASyntheticCtrlIsHeldIsBlockedThenProceedsAfterRelease()
    {
        // The plan's headline case. Right Ctrl is the default hotkey, so a finger still on it at
        // injection time is the normal case, not the exotic one.
        var keys = new FakeAsyncKeyState();
        keys.HoldForPolls(VirtualKeys.RightControl, polls: 3);
        var send = new FakeSendInput { QueryCounter = () => keys.TotalQueries };
        var injector = new SendInputInjector(
            new FakeFocusTracker(Notepad), new FakeWindowLiveness(), send, new ModifierGate(keys, FastGate));

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("hello", send.DecodeText());

        // Nothing left the injector until the gate had polled Ctrl up on the fourth round.
        Assert.All(send.KeyStateQueriesAtSend, queries => Assert.True(
            queries >= ModifierGate.ModifierVirtualKeys.Count * 4,
            "text was sent before the gate saw Ctrl released"));
    }

    [Fact]
    public async Task AModifierHeldPastTheDeadlineAbortsAndTypesAbsolutelyNothing()
    {
        var keys = new FakeAsyncKeyState();
        keys.HoldForever(VirtualKeys.RightControl);
        var send = new FakeSendInput();
        var injector = new SendInputInjector(
            new FakeFocusTracker(Notepad), new FakeWindowLiveness(), send, new ModifierGate(keys, FastGate));

        var result = await injector.InjectAsync("rm -rf /", Notepad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectionFailure.ModifierHeld, result.Failure);
        Assert.Equal(0, result.CharactersSent);
        Assert.Empty(send.AllRecords);
        Assert.Contains("Right Ctrl", result.Detail!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- SendInput: the target window

    [Fact]
    public async Task FocusChangeBetweenKeyDownAndInjectAbortsAndInjectsNothing()
    {
        var moved = Notepad with { Handle = 0x00CAFE, ProcessId = 99, ProcessName = "chrome" };
        var send = new FakeSendInput();
        var injector = new SendInputInjector(
            new FakeFocusTracker(moved), new FakeWindowLiveness(), send, new ModifierGate(new FakeAsyncKeyState(), FastGate));

        var result = await injector.InjectAsync("bank password", Notepad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectionFailure.TargetChanged, result.Failure);
        Assert.Empty(send.AllRecords);
        Assert.Contains("chrome", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARecycledHandleInADifferentProcessCountsAsAChangedTarget()
    {
        // HWNDs are recycled. The handle alone matching is not identity -- that is exactly what
        // TargetWindow.MatchesIdentity exists to catch.
        var recycled = Notepad with { ProcessId = 7, ProcessName = "chrome" };
        var send = new FakeSendInput();
        var injector = new SendInputInjector(
            new FakeFocusTracker(recycled), new FakeWindowLiveness(), send, new ModifierGate(new FakeAsyncKeyState(), FastGate));

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.Equal(InjectionFailure.TargetChanged, result.Failure);
        Assert.Empty(send.AllRecords);
    }

    [Fact]
    public async Task AClosedTargetWindowAbortsAsTargetGone()
    {
        var send = new FakeSendInput();
        var injector = new SendInputInjector(
            new FakeFocusTracker(Notepad),
            new FakeWindowLiveness { Alive = false },
            send,
            new ModifierGate(new FakeAsyncKeyState(), FastGate));

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.Equal(InjectionFailure.TargetGone, result.Failure);
        Assert.Empty(send.AllRecords);
    }

    [Fact]
    public async Task AnUncapturedTargetAbortsRatherThanTypingIntoWhateverHasFocusNow()
    {
        var send = new FakeSendInput();
        var injector = NewSendInputInjector(send, out _);

        var result = await injector.InjectAsync("hello", TargetWindow.None, TestContext.Current.CancellationToken);

        Assert.Equal(InjectionFailure.TargetGone, result.Failure);
        Assert.Empty(send.AllRecords);
    }

    // ---------------------------------------------------------------- SendInput: refusals from Win32

    [Fact]
    public async Task UipiRefusalIsReportedAsPrivilegeBlockedRatherThanUnknown()
    {
        // An elevated target with no uiAccess. The remedy is specific, so the failure must be too.
        var send = new FakeSendInput
        {
            OnSend = _ => new SendInputOutcome(0, SendInputOutcome.ErrorAccessDenied),
        };
        var injector = NewSendInputInjector(send, out _);

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectionFailure.PrivilegeBlocked, result.Failure);
    }

    [Fact]
    public async Task APartialAcceptForAnyOtherReasonIsReportedAsUnknown()
    {
        var send = new FakeSendInput { OnSend = batch => new SendInputOutcome((uint)batch.Length - 1, 1400) };
        var injector = NewSendInputInjector(send, out _);

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectionFailure.Unknown, result.Failure);
    }

    // ---------------------------------------------------------------- Clipboard strategy

    [Fact]
    public async Task ClipboardContentsAndFormatListAreRestoredExactly()
    {
        var clipboard = FakeClipboard.WithUsersData();
        var before = clipboard.Snapshot();
        var send = new FakeSendInput();
        var injector = NewClipboardInjector(clipboard, send);

        var result = await injector.InjectAsync("dictated text", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(InjectionStrategy.Clipboard, result.Strategy);
        AssertClipboardMatches(before, clipboard.Snapshot());
    }

    [Fact]
    public async Task ClipboardIsRestoredEvenWhenTheInjectionThrowsHalfwayThrough()
    {
        // The clipboard is global state owned by the user. Jane borrowing it and then failing
        // must not cost them what they had copied.
        var clipboard = FakeClipboard.WithUsersData();
        var before = clipboard.Snapshot();
        var send = new FakeSendInput { OnSend = _ => throw new InvalidOperationException("forced mid-inject failure") };
        var injector = NewClipboardInjector(clipboard, send);

        var result = await injector.InjectAsync("dictated text", Notepad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectionFailure.Unknown, result.Failure);
        Assert.Contains("forced mid-inject failure", result.Detail!, StringComparison.Ordinal);
        AssertClipboardMatches(before, clipboard.Snapshot());
    }

    [Fact]
    public async Task DelayedRenderFormatsAreNeverRead()
    {
        // Calling GetClipboardData on a delayed-render format makes the source app render it
        // synchronously, which can hang that app -- and Jane with it. The fake throws if one is
        // touched, so this test fails loudly rather than hanging.
        var clipboard = FakeClipboard.WithUsersData();
        clipboard.AddDelayedRenderFormat(clipboard.RegisterFormat("Some Huge Owner-Rendered Format"));
        var send = new FakeSendInput();
        var injector = NewClipboardInjector(clipboard, send);

        var result = await injector.InjectAsync("dictated text", Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Empty(clipboard.DelayedRenderFormatsRead);
    }

    [Fact]
    public async Task ThePastedPayloadCarriesTheTextAndTheClipboardHistoryOptOuts()
    {
        var clipboard = new FakeClipboard();
        var send = new FakeSendInput();
        var injector = NewClipboardInjector(clipboard, send);

        await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        var pasted = clipboard.SetContentsCalls[0];
        var text = pasted.Single(p => p.Format == ClipboardFormats.UnicodeText);
        Assert.Equal("hello\0", Encoding.Unicode.GetString(text.Data));

        foreach (var name in ClipboardFormats.HistoryOptOutFormatNames)
        {
            var format = clipboard.RegisterFormat(name);
            var payload = pasted.Single(p => p.Format == format);
            Assert.Equal(0u, BitConverter.ToUInt32(payload.Data));
        }
    }

    [Fact]
    public async Task ThePasteIsASyntheticCtrlVWithBothKeysReleasedAfterwards()
    {
        var clipboard = new FakeClipboard();
        var send = new FakeSendInput();
        var injector = NewClipboardInjector(clipboard, send);

        await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        var records = send.AllRecords;
        Assert.Equal(4, records.Count);
        Assert.Equal(VirtualKeys.Control, records[0].Keyboard.VirtualKey);
        Assert.Equal(0u, records[0].Keyboard.Flags & KeyEventFlags.KeyUp);
        Assert.Equal(VirtualKeys.KeyV, records[1].Keyboard.VirtualKey);
        Assert.Equal(VirtualKeys.KeyV, records[2].Keyboard.VirtualKey);
        Assert.NotEqual(0u, records[2].Keyboard.Flags & KeyEventFlags.KeyUp);
        Assert.Equal(VirtualKeys.Control, records[3].Keyboard.VirtualKey);
        Assert.NotEqual(0u, records[3].Keyboard.Flags & KeyEventFlags.KeyUp);
    }

    [Fact]
    public async Task AHeldModifierAbortsBeforeTheClipboardIsEverTouched()
    {
        var keys = new FakeAsyncKeyState();
        keys.HoldForever(VirtualKeys.LeftAlt);
        var clipboard = FakeClipboard.WithUsersData();
        var send = new FakeSendInput();
        var injector = new ClipboardInjector(
            new FakeFocusTracker(Notepad), new FakeWindowLiveness(), clipboard, send,
            new ModifierGate(keys, FastGate), NoSettle);

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.Equal(InjectionFailure.ModifierHeld, result.Failure);
        Assert.Equal(InjectionStrategy.Clipboard, result.Strategy);
        Assert.Empty(clipboard.SetContentsCalls);
        Assert.Empty(send.AllRecords);
    }

    [Fact]
    public async Task AChangedTargetAbortsBeforeTheClipboardIsEverTouched()
    {
        var clipboard = FakeClipboard.WithUsersData();
        var send = new FakeSendInput();
        var injector = new ClipboardInjector(
            new FakeFocusTracker(Notepad with { ProcessId = 5, ProcessName = "chrome" }),
            new FakeWindowLiveness(), clipboard, send,
            new ModifierGate(new FakeAsyncKeyState(), FastGate), NoSettle);

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.Equal(InjectionFailure.TargetChanged, result.Failure);
        Assert.Empty(clipboard.SetContentsCalls);
    }

    [Fact]
    public async Task AnEmptyClipboardIsLeftEmpty()
    {
        var clipboard = new FakeClipboard();
        var injector = NewClipboardInjector(clipboard, new FakeSendInput());

        await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.Empty(clipboard.GetAvailableFormats());
    }

    [Fact]
    public async Task AnUnreadableClipboardAbandonsTheStrategyRatherThanGuessing()
    {
        var clipboard = new FakeClipboard { FailEnumeration = true };
        var send = new FakeSendInput();
        var injector = NewClipboardInjector(clipboard, send);

        var result = await injector.InjectAsync("hello", Notepad, TestContext.Current.CancellationToken);

        Assert.Equal(InjectionFailure.ClipboardUnavailable, result.Failure);
        Assert.Empty(send.AllRecords);
    }

    [Fact]
    public async Task LongTextSurvivesTheClipboardRoundTripUnchanged()
    {
        var text = LongTextWithEmoji();
        var clipboard = new FakeClipboard();
        var injector = NewClipboardInjector(clipboard, new FakeSendInput());

        var result = await injector.InjectAsync(text, Notepad, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(text.Length, result.CharactersSent);
        var pasted = clipboard.SetContentsCalls[0].Single(p => p.Format == ClipboardFormats.UnicodeText);
        Assert.Equal(text + "\0", Encoding.Unicode.GetString(pasted.Data));
    }

    // ---------------------------------------------------------------- Routing between strategies

    [Fact]
    public async Task ARouterTypesIntoATerminalNoMatterHowLongTheTextIs()
    {
        var terminal = Notepad with { ProcessName = "windowsterminal", WindowClass = "CASCADIA_HOSTING_WINDOW_CLASS" };
        var clipboard = FakeClipboard.WithUsersData();
        var send = new FakeSendInput();
        var router = NewRouter(clipboard, send, terminal);

        var result = await router.InjectAsync(LongTextWithEmoji(), terminal, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(InjectionStrategy.Unicode, result.Strategy);
        Assert.Empty(clipboard.SetContentsCalls);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Fact]
    public async Task ARouterPastesLongTextIntoABrowser()
    {
        var chrome = Notepad with { ProcessName = "chrome", WindowClass = "Chrome_WidgetWin_1" };
        var clipboard = FakeClipboard.WithUsersData();
        var send = new FakeSendInput();
        var router = NewRouter(clipboard, send, chrome);

        var result = await router.InjectAsync(LongTextWithEmoji(), chrome, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(InjectionStrategy.Clipboard, result.Strategy);
        Assert.NotEmpty(clipboard.SetContentsCalls);
    }

    [Fact]
    public async Task AnUnusableClipboardFallsBackToTypingRatherThanLosingTheDictation()
    {
        // Another app holding the clipboard is transient and common. Losing a finished
        // dictation to it would be the wrong trade -- typing still works.
        var chrome = Notepad with { ProcessName = "chrome" };
        var clipboard = new FakeClipboard { FailEnumeration = true };
        var send = new FakeSendInput();
        var router = NewRouter(clipboard, send, chrome);

        var result = await router.InjectAsync("a fairly long dictated sentence " + new string('x', 400), chrome, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(InjectionStrategy.Unicode, result.Strategy);
        Assert.Contains("clipboard", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARouterDoesNotRetryAFailureThatWouldFailTheSameWayTwice()
    {
        var keys = new FakeAsyncKeyState();
        keys.HoldForever(VirtualKeys.RightControl);
        var clipboard = FakeClipboard.WithUsersData();
        var send = new FakeSendInput();
        var chrome = Notepad with { ProcessName = "chrome" };
        var router = new RoutingTextInjector(
            new InjectionStrategySelector(),
            new SendInputInjector(new FakeFocusTracker(chrome), new FakeWindowLiveness(), send, new ModifierGate(keys, FastGate)),
            new ClipboardInjector(new FakeFocusTracker(chrome), new FakeWindowLiveness(), clipboard, send, new ModifierGate(keys, FastGate), NoSettle));

        var result = await router.InjectAsync(new string('x', 400), chrome, TestContext.Current.CancellationToken);

        Assert.Equal(InjectionFailure.ModifierHeld, result.Failure);
        Assert.Empty(send.AllRecords);
        Assert.Empty(clipboard.SetContentsCalls);
    }

    // ---------------------------------------------------------------- Layout and real Win32

    [Fact]
    public void TheInputRecordMatchesTheSizeSendInputExpectsOnX64()
    {
        // SendInput rejects the whole batch, silently, if cbSize disagrees with its own INPUT.
        Assert.Equal(40, Marshal.SizeOf<InputRecord>());
        Assert.Equal(40, Win32SendInput.RecordSize);
        Assert.Equal(8, Marshal.OffsetOf<InputRecord>(nameof(InputRecord.Keyboard)).ToInt32());
    }

    [Fact]
    public void ADeadHandleIsReportedAsNotAlive()
    {
        Assert.False(new FocusedAppIdentity().IsAlive(0x1));
        Assert.False(new FocusedAppIdentity().IsAlive(0));
    }

    [Fact]
    public void TheRealFocusTrackerDescribesWhateverHasFocus()
    {
        var identity = new FocusedAppIdentity();
        var target = identity.GetForegroundWindow();
        Assert.SkipWhen(target.IsNone, "No foreground window: this session is not interactive.");

        Assert.NotEqual(0, target.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(target.ProcessName));
        Assert.Equal(target.ProcessName.ToLowerInvariant(), target.ProcessName);
        Assert.DoesNotContain(".exe", target.ProcessName, StringComparison.OrdinalIgnoreCase);
        Assert.True(identity.IsAlive(target.Handle));
        Assert.True(target.MatchesIdentity(target));
    }

    [Fact]
    public void TheRealKeyStateReadsWithoutThrowing()
    {
        // Reading is always safe; it is only writing that would escape into the session.
        var state = new Win32AsyncKeyState();

        Assert.All(ModifierGate.ModifierVirtualKeys, vk => state.IsPhysicallyDown(vk));
    }

    [Theory]
    [InlineData("user32.dll", "SendInput")]
    [InlineData("user32.dll", "GetAsyncKeyState")]
    [InlineData("user32.dll", "GetForegroundWindow")]
    [InlineData("user32.dll", "IsWindow")]
    [InlineData("user32.dll", "GetWindowThreadProcessId")]
    [InlineData("user32.dll", "GetClassNameW")]
    [InlineData("user32.dll", "GetWindowTextW")]
    [InlineData("user32.dll", "RegisterClipboardFormatW")]
    [InlineData("user32.dll", "OpenClipboard")]
    [InlineData("user32.dll", "CloseClipboard")]
    [InlineData("user32.dll", "EmptyClipboard")]
    [InlineData("user32.dll", "EnumClipboardFormats")]
    [InlineData("user32.dll", "GetClipboardData")]
    [InlineData("user32.dll", "SetClipboardData")]
    [InlineData("kernel32.dll", "OpenProcess")]
    [InlineData("kernel32.dll", "QueryFullProcessImageNameW")]
    [InlineData("kernel32.dll", "CloseHandle")]
    [InlineData("kernel32.dll", "GlobalAlloc")]
    [InlineData("kernel32.dll", "GlobalFree")]
    [InlineData("kernel32.dll", "GlobalLock")]
    [InlineData("kernel32.dll", "GlobalUnlock")]
    [InlineData("kernel32.dll", "GlobalSize")]
    public void EveryWin32EntryPointJaneBindsToResolves(string library, string entryPoint)
    {
        // LibraryImport does no A/W suffix probing, so a missing "W" is a runtime DllNotFound
        // in the middle of a dictation rather than a compile error. This is the cheapest place
        // to catch that, and the only one that does not require sending real input.
        var module = NativeLibrary.Load(library);
        try
        {
            Assert.True(NativeLibrary.TryGetExport(module, entryPoint, out var address));
            Assert.NotEqual(0, address);
        }
        finally
        {
            NativeLibrary.Free(module);
        }
    }

    [Fact]
    public void TheRealClipboardCanBeInspectedWithoutChangingIt()
    {
        // Read-only on purpose: a test that set the clipboard would destroy whatever the
        // developer had copied. The absent-format probe exercises GetClipboardData against a
        // format nothing can have published, so no delayed render can be triggered.
        var clipboard = new Win32Clipboard();
        var before = TryEnumerate(clipboard);
        Assert.SkipWhen(before is null, "The clipboard is held by another process in this session.");

        var neverPresent = clipboard.RegisterFormat("Jane.Probe.NeverPresent");
        Assert.NotEqual(0u, neverPresent);
        Assert.Null(clipboard.TryGetFormatData(neverPresent));
        Assert.Equal(before, clipboard.GetAvailableFormats());

        static IReadOnlyList<uint>? TryEnumerate(Win32Clipboard clipboard)
        {
            try
            {
                return clipboard.GetAvailableFormats();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static SendInputInjector NewSendInputInjector(
        FakeSendInput send, out FakeAsyncKeyState keys, SendInputInjectorOptions? options = null)
    {
        keys = new FakeAsyncKeyState();
        return new SendInputInjector(
            new FakeFocusTracker(Notepad), new FakeWindowLiveness(), send,
            new ModifierGate(keys, FastGate), options);
    }

    private static ClipboardInjector NewClipboardInjector(FakeClipboard clipboard, FakeSendInput send) =>
        new(new FakeFocusTracker(Notepad), new FakeWindowLiveness(), clipboard, send,
            new ModifierGate(new FakeAsyncKeyState(), FastGate), NoSettle);

    private static RoutingTextInjector NewRouter(FakeClipboard clipboard, FakeSendInput send, TargetWindow target)
    {
        var focus = new FakeFocusTracker(target);
        var liveness = new FakeWindowLiveness();
        var gate = new ModifierGate(new FakeAsyncKeyState(), FastGate);
        return new RoutingTextInjector(
            new InjectionStrategySelector(),
            new SendInputInjector(focus, liveness, send, gate),
            new ClipboardInjector(focus, liveness, clipboard, send, gate, NoSettle));
    }

    private static string LongTextWithEmoji()
    {
        // Escaped rather than literal so the assertion cannot be weakened by how this source
        // file happens to be decoded -- the point of the test is the exact UTF-16 code units.
        const string seed =
            "Jane types: na\u00EFve caf\u00E9 r\u00E9sum\u00E9 \u2014 \u65E5\u672C\u8A9E " +
            "\U0001F600\U0001F389 stra\u00DFe \u00BD \u03C0 \u2211 ";
        var builder = new StringBuilder();
        while (builder.Length < 500)
        {
            builder.Append(seed);
        }

        var text = builder.ToString();
        var cut = 500;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut++;
        }

        return text[..cut];
    }

    private static bool SplitsASurrogatePair(InputRecord[] batch)
    {
        var last = batch[^1].Keyboard;
        var first = batch[0].Keyboard;
        var lastIsHigh = (last.Flags & KeyEventFlags.Unicode) != 0 && char.IsHighSurrogate((char)last.ScanCode);
        var firstIsLow = (first.Flags & KeyEventFlags.Unicode) != 0 && char.IsLowSurrogate((char)first.ScanCode);
        return lastIsHigh || firstIsLow;
    }

    private static void AssertClipboardMatches(
        IReadOnlyList<ClipboardPayload> before, IReadOnlyList<ClipboardPayload> after)
    {
        Assert.Equal(
            before.Select(p => p.Format).ToArray(),
            after.Select(p => p.Format).ToArray());
        Assert.All(before.Zip(after), pair => Assert.Equal(pair.First.Data, pair.Second.Data));
    }
}

/// <summary>Records the <c>INPUT</c> stream instead of pushing it at the real keyboard.</summary>
internal sealed class FakeSendInput : ISendInput
{
    public List<InputRecord[]> Batches { get; } = [];

    /// <summary>The number of key-state queries seen at the moment of each batch, for ordering assertions.</summary>
    public List<int> KeyStateQueriesAtSend { get; } = [];

    public Func<InputRecord[], SendInputOutcome>? OnSend { get; init; }

    public Func<int>? QueryCounter { get; init; }

    public IReadOnlyList<InputRecord> AllRecords => Batches.SelectMany(b => b).ToArray();

    public SendInputOutcome Send(ReadOnlySpan<InputRecord> inputs)
    {
        var batch = inputs.ToArray();
        KeyStateQueriesAtSend.Add(QueryCounter?.Invoke() ?? 0);
        Batches.Add(batch);
        return OnSend?.Invoke(batch) ?? new SendInputOutcome((uint)batch.Length, 0);
    }

    /// <summary>
    /// Rebuilds the string Windows would have produced: one character per key-down record,
    /// key-ups skipped, VK_RETURN read back as the line break it stands for.
    /// </summary>
    public string DecodeText()
    {
        var builder = new StringBuilder();
        foreach (var record in AllRecords)
        {
            if ((record.Keyboard.Flags & KeyEventFlags.KeyUp) != 0)
            {
                continue;
            }

            if ((record.Keyboard.Flags & KeyEventFlags.Unicode) != 0)
            {
                builder.Append((char)record.Keyboard.ScanCode);
            }
            else if (record.Keyboard.VirtualKey == VirtualKeys.Return)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }
}

internal sealed class FakeFocusTracker(TargetWindow current) : IFocusTracker
{
    public TargetWindow GetForegroundWindow() => current;
}

internal sealed class FakeWindowLiveness : IWindowLiveness
{
    public bool Alive { get; init; } = true;

    public bool IsAlive(nint handle) => Alive;
}

/// <summary>
/// An in-memory clipboard that fails loudly if a delayed-render format is read.
/// </summary>
/// <remarks>
/// On the real clipboard, reading such a format asks the owning app to render it synchronously,
/// which is what can hang the source app. Here it throws instead, which turns a hang into a
/// test failure with a stack trace.
/// </remarks>
internal sealed class FakeClipboard : IClipboard
{
    private readonly Dictionary<string, uint> _registered = [];
    private readonly List<ClipboardPayload> _contents = [];
    private readonly HashSet<uint> _delayedRender = [];
    private uint _nextRegisteredFormat = 0xC000;

    public bool FailEnumeration { get; init; }

    public List<IReadOnlyList<ClipboardPayload>> SetContentsCalls { get; } = [];

    public List<uint> DelayedRenderFormatsRead { get; } = [];

    public static FakeClipboard WithUsersData()
    {
        var clipboard = new FakeClipboard();
        clipboard._contents.Add(new ClipboardPayload(ClipboardFormats.UnicodeText, Encoding.Unicode.GetBytes("the user's own copied text\0")));
        clipboard._contents.Add(new ClipboardPayload(ClipboardFormats.Text, "the user's own copied text\0"u8.ToArray()));
        clipboard._contents.Add(new ClipboardPayload(clipboard.RegisterFormat("HTML Format"), "<b>rich</b>"u8.ToArray()));
        clipboard._contents.Add(new ClipboardPayload(ClipboardFormats.Locale, BitConverter.GetBytes(0x0409u)));
        return clipboard;
    }

    public void AddDelayedRenderFormat(uint format)
    {
        _delayedRender.Add(format);
        _contents.Add(new ClipboardPayload(format, []));
    }

    /// <summary>
    /// Bumped on every read and every write, as Windows bumps its own on every clipboard open.
    /// </summary>
    /// <remarks>
    /// The paste-settle wait watches this to learn that the target opened the clipboard. A fake
    /// that never moved it would make every test take the ceiling, and one that moved it on its
    /// own would make the wait look like it confirmed something it did not.
    /// </remarks>
    public uint SequenceNumber { get; private set; } = 1;

    /// <summary>Stands in for the target reading the paste, which is what moves the real number.</summary>
    public void SimulateRead() => SequenceNumber++;

    /// <summary>Windows bumps the number on every open, Jane's own writes included.</summary>
    private void Touch() => SequenceNumber++;

    public IReadOnlyList<ClipboardPayload> Snapshot() => [.. _contents];

    public uint RegisterFormat(string name)
    {
        if (!_registered.TryGetValue(name, out var format))
        {
            format = _nextRegisteredFormat++;
            _registered[name] = format;
        }

        return format;
    }

    public IReadOnlyList<uint> GetAvailableFormats() => FailEnumeration
        ? throw new InvalidOperationException("the clipboard is locked by another process")
        : [.. _contents.Select(p => p.Format)];

    public byte[]? TryGetFormatData(uint format)
    {
        if (_delayedRender.Contains(format))
        {
            DelayedRenderFormatsRead.Add(format);
            throw new InvalidOperationException(
                $"format {format} is delayed-render; reading it would block on the source app");
        }

        return _contents.FirstOrDefault(p => p.Format == format)?.Data;
    }

    public void SetContents(IReadOnlyList<ClipboardPayload> payloads)
    {
        Touch();
        SetContentsCalls.Add([.. payloads]);
        _contents.Clear();
        _contents.AddRange(payloads);
    }
}
