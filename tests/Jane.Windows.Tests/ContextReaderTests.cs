using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Jane.Core.Abstractions;
using Jane.Core.Vocabulary;
using Jane.Windows.Automation;

namespace Jane.Windows.Tests;

/// <summary>
/// Deep Context against real UI Automation, and the term extraction that turns a read into
/// hotwords.
/// </summary>
/// <remarks>
/// <para>
/// The plan asks for reads "against Chrome, VS Code and Windows Terminal, not only a WPF harness",
/// and that is what the acceptance tests here do -- they launch the real applications, put real
/// text on screen, and read it through the shipped code path. Every one of them skips with a
/// stated reason rather than failing when the application is absent or the session has no
/// interactive desktop, because a machine without Chrome installed is not a broken build.
/// </para>
/// <para>
/// The local harness is a real Win32 window with a real <c>EDIT</c> control rather than a WPF one:
/// <c>Jane.Windows</c> sets <c>UseWPF=false</c>, so this test project has no WPF to build a
/// TextBox with, and the <c>EDIT</c> control exercises the identical client path -- UI Automation's
/// standard-control provider gives it the same <c>TextPattern</c> and <c>ValuePattern</c> a WPF
/// TextBox exposes. It also gives the phase something WPF could not: a provider this test can
/// deliberately wedge.
/// </para>
/// </remarks>
// Serialised with every other foreground-owning test in the solution. These read the *focused*
// element through UIA, so another test project bringing its own window forward mid-read makes them
// fail for reasons unrelated to the code. See ForegroundLock.
[Collection("Foreground")]
public sealed class ContextReaderTests : IDisposable
{
    private readonly ForegroundLock _foreground = ForegroundLock.Acquire();

    public void Dispose() => _foreground.Dispose();

    private const string Fixture =
        "Deploy the Kubernetes manifest to the Grafana dashboard. Ask Kate Chen to review UiaWorker and kube_proxy.";

    // =====================================================================================
    // Term extraction -- pure, and the part every consumer downstream actually receives.
    // =====================================================================================

    [Fact]
    public void ProperNounsAndCodeIdentifiersOutrankOrdinaryWords()
    {
        var hints = ContextTermExtractor.Extract(Fixture, null, null);

        Assert.Contains("Kubernetes", hints.Terms);
        Assert.Contains("Grafana", hints.Terms);
        Assert.Contains("UiaWorker", hints.Terms);
        Assert.Contains("kube_proxy", hints.Terms);
        Assert.DoesNotContain("the", hints.Terms);
        Assert.DoesNotContain("to", hints.Terms);

        // The code identifiers are the terms no general speech model has seen spelled that way,
        // so they must be at the front of a list that gets truncated.
        var identifier = hints.Terms.ToList().IndexOf("UiaWorker");
        var jargon = hints.Terms.ToList().IndexOf("manifest");
        Assert.True(identifier < jargon || jargon < 0, "A code identifier ranked below ordinary jargon.");
    }

    [Fact]
    public void TheTermListIsCappedSoEachTermKeepsItsBiasingWeight()
    {
        // Contextual biasing spreads a fixed budget over the hotword list. A screen dump would
        // dilute every real term toward nothing, so the cap is part of the accuracy claim.
        var wall = string.Join(' ', Enumerable.Range(0, 400).Select(i => $"Term{i}Alpha"));

        var hints = ContextTermExtractor.Extract(wall, null, null);

        Assert.Equal(ContextExtractionOptions.Default.MaxTerms, hints.Terms.Count);
    }

    [Fact]
    public void SelectionOutranksSurroundingText()
    {
        // What the user selected is what they are looking at, so it is the best evidence there is.
        var hints = ContextTermExtractor.Extract(
            selection: "Reviewing Grafana today.",
            surrounding: "Kubernetes appears here in the wider document.",
            controlValue: null);

        Assert.True(
            hints.Terms.ToList().IndexOf("Grafana") < hints.Terms.ToList().IndexOf("Kubernetes"),
            "A term from the surrounding paragraph outranked one from the selection.");
        Assert.Equal(ContextSource.Mixed, hints.Source);
    }

    [Fact]
    public void SecretsAndOpaqueTokensNeverBecomeHints()
    {
        var hints = ContextTermExtractor.Extract(
            "Kubernetes token 3f9a2b7c8d1e4f6a0b2c4d6e8f0a1b2c and kate@example.com and 4111111111111111",
            null,
            null);

        Assert.Contains("Kubernetes", hints.Terms);
        Assert.DoesNotContain(hints.Terms, term => term.Contains('@', StringComparison.Ordinal));
        Assert.DoesNotContain(hints.Terms, term => term.All(char.IsDigit));
        Assert.DoesNotContain("3f9a2b7c8d1e4f6a0b2c4d6e8f0a1b2c", hints.Terms);
    }

    [Fact]
    public void ExtractionIsDeterministicSoTheEvalCorpusIsReproducible()
    {
        var first = ContextTermExtractor.Extract(Fixture, Fixture, Fixture);
        var second = ContextTermExtractor.Extract(Fixture, Fixture, Fixture);

        Assert.Equal(first.Terms, second.Terms);
    }

    [Fact]
    public void NothingOnScreenProducesNothingToSay()
    {
        var hints = ContextTermExtractor.Extract("   ", null, null);

        Assert.True(hints.IsEmpty);
        Assert.Equal(string.Empty, hints.ToPromptFragment());
        Assert.Empty(ContextHints.Empty.Hotwords);
    }

    [Fact]
    public void ThePromptFragmentNamesTheTermsAndNothingElse()
    {
        // Terms, never prose. A model handed a paragraph plus a transcript tends to answer the
        // paragraph instead of formatting the transcript, so the surrounding text contributes
        // vocabulary to the fragment and never a sentence.
        const string Prose = "Grafana is mentioned again much further down the surrounding paragraph.";
        var hints = ContextTermExtractor.Extract("Grafana", Prose, null);

        var fragment = hints.ToPromptFragment();

        Assert.Contains("Grafana", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain(Prose, fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("is mentioned again", fragment, StringComparison.Ordinal);
    }

    // =====================================================================================
    // The real COM client.
    // =====================================================================================

    [Fact]
    public void TheConnectionTimeoutRoundTripsThroughTheRealComObject()
    {
        // The one runtime check on a hand-derived vtable layout. IUIAutomation2's timeout
        // properties sit at slots 60 and 61, past fifty-five inherited methods; if that arithmetic
        // were wrong, this would not come back with the value it was given.
        UiaComSession? session = null;
        uint? roundTrip = null;
        var thread = new Thread(() =>
        {
            session = new UiaComSession(new UiaComOptions(ConnectionTimeoutMs: 137));
            roundTrip = session.ConnectionTimeoutRoundTrips();
        });

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));

        Assert.SkipWhen(session is null || !session.IsAvailable, "UI Automation could not be created in this session.");
        Assert.True(session!.SupportsTimeouts, "This machine did not offer IUIAutomation2.");
        Assert.Equal(137u, roundTrip);

        session.Dispose();
    }

    [Fact]
    public void ReadsTheSelectionFromALocalTextHarness()
    {
        using var harness = Win32TextHarness.Create(Fixture);
        Assert.SkipWhen(harness is null, "No interactive desktop: the harness window could not take foreground.");

        harness!.SelectAll();

        using var reader = RealReader(out var worker);
        var read = reader.BeginRead(harness.Target).Wait(TimeSpan.FromSeconds(5));

        Assert.Equal(ContextOutcome.Read, read.Outcome);
        Assert.Contains("Kubernetes", read.Hints.Terms);
        Assert.Contains("Grafana", read.Hints.Terms);
        Assert.NotNull(read.Hints.Selection);
        Assert.Contains("Kubernetes", read.Hints.Selection!, StringComparison.Ordinal);
        worker.Dispose();
    }

    [Fact]
    public void ReadsTheParagraphAroundTheCaretWhenNothingIsSelected()
    {
        using var harness = Win32TextHarness.Create(Fixture);
        Assert.SkipWhen(harness is null, "No interactive desktop: the harness window could not take foreground.");

        harness!.PlaceCaretAt(10);

        using var reader = RealReader(out var worker);
        var read = reader.BeginRead(harness.Target).Wait(TimeSpan.FromSeconds(5));
        worker.Dispose();

        Assert.Contains(read.Outcome, (ContextOutcome[])[ContextOutcome.Read, ContextOutcome.Empty]);
        Assert.Contains("Kubernetes", read.Hints.Terms);
    }

    [Fact]
    public void APasswordStyledControlIsNeverRead()
    {
        // ES_PASSWORD is what makes UI Automation report IsPassword, which is the only signal a
        // client gets. The assertion is not "the text was dropped" but "no text came back at all".
        using var harness = Win32TextHarness.Create("hunter2 correcthorsebatterystaple", password: true);
        Assert.SkipWhen(harness is null, "No interactive desktop: the harness window could not take foreground.");

        harness!.SelectAll();

        using var reader = RealReader(out var worker);
        var read = reader.BeginRead(harness.Target).Wait(TimeSpan.FromSeconds(5));
        worker.Dispose();

        Assert.Equal(ContextOutcome.PasswordControl, read.Outcome);
        Assert.True(read.Hints.IsEmpty);
        Assert.Null(read.Hints.Selection);
        Assert.Null(read.Hints.Surrounding);
    }

    [Fact]
    public void ABlocklistedProcessMakesNoCallEvenAgainstTheRealProvider()
    {
        using var reader = RealReader(out var worker);

        var read = reader.BeginRead(new TargetWindow(1, 2, "1password", "Window", "1Password — Vault")).Wait();

        Assert.Equal(ContextOutcome.Blocked, read.Outcome);
        Assert.True(read.Hints.IsEmpty);
        Assert.Equal(0, worker.SubmittedReads);
        worker.Dispose();
    }

    [Fact]
    public void ABrowserOnABlocklistedUrlYieldsEmptyHints()
    {
        // Two layers, both asserted. The title is all a client gets before deciding whether to
        // look at a window at all; the URL only becomes visible once something has been read.
        using var reader = RealReader(out var worker);

        var byTitle = reader.BeginRead(
            new TargetWindow(1, 2, "chrome", "Chrome_WidgetWin_1", "Chase Online - Log In - Google Chrome")).Wait();

        Assert.Equal(ContextOutcome.Blocked, byTitle.Outcome);
        Assert.True(byTitle.Hints.IsEmpty);
        Assert.Equal(0, worker.SubmittedReads);

        var byContent = reader.Blocklist.EvaluateContent("https://secure.chase.com/web/auth/dashboard");
        Assert.True(byContent.IsBlocked);
        Assert.Equal(BlockReason.BlockedContent, byContent.Reason);
        worker.Dispose();
    }

    [Fact]
    public void AWindowThatStopsPumpingIsAbandonedAtTheDeadlineAndBlocklistedOnTheNextPress()
    {
        // The hanging provider, for real: a window whose thread is stuck inside its own window
        // procedure, so UI Automation's call to it never comes back. The connection timeout is set
        // high on purpose -- the point is that the worker's own deadline is what protects the
        // pipeline, not UI Automation's.
        using var harness = Win32TextHarness.Create(Fixture);
        Assert.SkipWhen(harness is null, "No interactive desktop: the harness window could not take foreground.");

        var target = harness!.Target;
        var blocklist = new Blocklist();
        var before = Process.GetCurrentProcess().Threads.Count;

        using var worker = UiaWorker.CreateDefault(
            new UiaComOptions(ConnectionTimeoutMs: 5_000, TransactionTimeoutMs: 10_000),
            new UiaWorkerOptions { WedgeAfter = TimeSpan.FromMilliseconds(80) });
        using var reader = new UiaContextReader(worker, blocklist, new ContextReadOptions
        {
            Deadline = TimeSpan.FromMilliseconds(80),
        });

        harness.Freeze();
        try
        {
            var timings = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var clock = Stopwatch.StartNew();
                var read = reader.BeginRead(target).Wait();
                timings.Add(clock.Elapsed.TotalMilliseconds);

                Assert.True(read.Hints.IsEmpty, $"A frozen provider produced hints: {read.Outcome}.");
                Assert.NotEqual(ContextOutcome.Read, read.Outcome);
            }

            var during = Process.GetCurrentProcess().Threads.Count;
            Assert.True(during - before <= 5, $"Thread count went from {before} to {during} over 20 wedged reads.");
            Assert.True(timings.Max() < 1_500, $"A wedged read took {timings.Max():F0} ms, far past the 80 ms deadline.");

            // Second press onward: the process that wedged is off for the session.
            Assert.Contains(target.ProcessName, blocklist.AutoBlocked);
            Assert.Equal(ContextOutcome.Blocked, reader.BeginRead(target).Wait().Outcome);
        }
        finally
        {
            harness.Thaw();
        }
    }

    [Fact]
    public void AWholeReadFitsInsideTheEightyMillisecondDeadline()
    {
        // The plan records UIA round-trip latency as an unknown and sizes the deadline at 80 ms
        // without a measurement behind it. This is the measurement: a full read -- focused element
        // with its cached properties, selection, text, caret rectangle and surrounding paragraph.
        using var harness = Win32TextHarness.Create(Fixture);
        Assert.SkipWhen(harness is null, "No interactive desktop: the harness window could not take foreground.");

        harness!.SelectAll();

        using var worker = UiaWorker.CreateDefault();
        using var reader = new UiaContextReader(worker, new Blocklist(), new ContextReadOptions
        {
            Deadline = TimeSpan.FromSeconds(5),
        });

        // The first read pays for creating the client and warming the provider, and is reported
        // separately for exactly the reason the plan reports cold ASR separately.
        var cold = reader.BeginRead(harness.Target).Wait();
        Assert.Equal(ContextOutcome.Read, cold.Outcome);

        var samples = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var read = reader.BeginRead(harness.Target).Wait();
            Assert.Equal(ContextOutcome.Read, read.Outcome);
            samples.Add(read.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        var p95 = samples[(int)(samples.Count * 0.95)];

        Report($"Local harness read: roundTrips={cold.RoundTrips} cold={cold.Elapsed.TotalMilliseconds:F1} ms " +
               $"p50={median:F1} ms p95={p95:F1} ms max={samples[^1]:F1} ms");

        Assert.True(p95 < 80, $"A full read's p95 was {p95:F1} ms, at or past the whole context deadline.");
    }

    [Fact]
    public void TheCaretLocatorFindsTheSystemCaretInALocalTextHarness()
    {
        using var harness = Win32TextHarness.Create(Fixture);
        Assert.SkipWhen(harness is null, "No interactive desktop: the harness window could not take foreground.");

        harness!.PlaceCaretAt(20);

        var caret = new CaretLocator().Locate(harness.Target);

        Assert.NotNull(caret);
        Assert.False(caret!.Value.IsEmpty);
        Assert.True(caret.Value.Height > 0, "The caret rectangle had no height.");
    }

    [Fact]
    public void TheCaretLocatorFallsBackToTheRectangleUiAutomationReported()
    {
        // Chromium and Electron draw their own caret and create no system one, so the UIA
        // rectangle is the only answer available there.
        var locator = new CaretLocator();
        var fallback = new CaretRect(400, 300, 2, 18);

        Assert.Equal(fallback, locator.Locate(TargetWindow.None, fallback));
        Assert.Null(locator.Locate(TargetWindow.None));
        Assert.Null(locator.Locate(TargetWindow.None, new CaretRect(0, 0, 0, 0)));
    }

    // =====================================================================================
    // Acceptance: the real applications the plan names.
    // =====================================================================================

    [Fact]
    public void ReadsContextFromChrome()
    {
        var chrome = FindChrome();
        Assert.SkipWhen(chrome is null, "Google Chrome is not installed on this machine.");

        var page = WriteFixturePage();
        using var profile = new TempDirectory();
        using var app = RealApp.Launch(
            chrome!,
            $"--user-data-dir=\"{profile.Path}\" --no-first-run --no-default-browser-check --disable-extensions " +
            $"--force-renderer-accessibility --new-window --window-size=1100,800 \"file:///{page.Replace('\\', '/')}\"",
            TimeSpan.FromSeconds(30));

        Assert.SkipWhen(app is null, "Chrome did not produce a foreground window in this session.");

        AssertReadsFixture(app!, "Chrome");
    }

    [Fact]
    public void ReadsContextFromVisualStudioCode()
    {
        var code = FindVsCode();
        Assert.SkipWhen(code is null, "Visual Studio Code is not installed on this machine.");

        using var profile = new TempDirectory();
        using var extensions = new TempDirectory();

        // Monaco only fills its accessible text area when accessibility support is on; left on
        // "auto" it waits to detect a screen reader and exposes nothing to read.
        var userDirectory = Directory.CreateDirectory(Path.Combine(profile.Path, "User"));
        File.WriteAllText(
            Path.Combine(userDirectory.FullName, "settings.json"),
            "{ \"editor.accessibilitySupport\": \"on\", \"workbench.startupEditor\": \"none\", \"telemetry.telemetryLevel\": \"off\" }");

        var document = Path.Combine(Path.GetTempPath(), $"jane-context-{Guid.NewGuid():N}.txt");
        File.WriteAllText(document, Fixture);

        using var app = RealApp.Launch(
            code!,
            $"--new-window --disable-extensions --disable-workspace-trust --user-data-dir \"{profile.Path}\" " +
            $"--extensions-dir \"{extensions.Path}\" \"{document}\"",
            TimeSpan.FromSeconds(45));

        Assert.SkipWhen(app is null, "VS Code did not produce a foreground window in this session.");

        try
        {
            AssertReadsFixture(app!, "VS Code");
        }
        finally
        {
            TryDelete(document);
        }
    }

    [Fact]
    public void ReadsContextFromWindowsTerminal()
    {
        var terminal = FindWindowsTerminal();
        Assert.SkipWhen(terminal is null, "Windows Terminal is not installed on this machine.");

        // The prompt itself carries the fixture words, so the caret line -- the one a text
        // provider reports for a terminal -- is guaranteed to contain them.
        using var app = RealApp.Launch(
            terminal!,
            "cmd /k prompt Kubernetes Grafana KateChen$G",
            TimeSpan.FromSeconds(30),
            windowProcessName: "WindowsTerminal");

        Assert.SkipWhen(app is null, "Windows Terminal did not produce a foreground window in this session.");

        AssertReadsFixture(app!, "Windows Terminal", expected: ["Kubernetes", "Grafana"]);
    }

    private static void AssertReadsFixture(RealApp app, string label, string[]? expected = null)
    {
        expected ??= ["Kubernetes", "Grafana"];

        using var worker = UiaWorker.CreateDefault();
        using var reader = new UiaContextReader(worker, new Blocklist(), new ContextReadOptions
        {
            Deadline = TimeSpan.FromSeconds(6),
        });

        ContextRead? best = null;
        var timings = new List<double>();

        // Applications settle asynchronously: Chromium builds its accessibility tree lazily and
        // VS Code finishes painting after its window appears. Retry rather than sleep for a fixed
        // and necessarily wrong amount of time.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (!app.Activate())
            {
                Thread.Sleep(500);
                continue;
            }

            var clock = Stopwatch.StartNew();
            var read = reader.BeginRead(app.Target).Wait();
            timings.Add(clock.Elapsed.TotalMilliseconds);

            if (best is null || read.Hints.Terms.Count > best.Hints.Terms.Count)
            {
                best = read;
            }

            if (expected.All(term => read.Hints.Terms.Contains(term, StringComparer.OrdinalIgnoreCase)))
            {
                break;
            }

            Thread.Sleep(750);
        }

        Assert.SkipWhen(best is null, $"{label} never held foreground long enough for a read.");

        Report($"{label}: outcome={best!.Outcome} terms=[{string.Join(", ", best.Hints.Terms.Take(8))}] " +
               $"roundTrips={best.RoundTrips} readMs={best.Elapsed.TotalMilliseconds:F1} " +
               $"waitMs=[{string.Join(", ", timings.Select(t => t.ToString("F0", CultureInfo.InvariantCulture)))}]");

        Assert.True(
            expected.All(term => best.Hints.Terms.Contains(term, StringComparer.OrdinalIgnoreCase)),
            $"{label} read produced {best.Outcome} with terms [{string.Join(", ", best.Hints.Terms)}]; " +
            $"expected [{string.Join(", ", expected)}]. Selection={Quote(best.Hints.Selection)} " +
            $"Surrounding={Quote(best.Hints.Surrounding)}");
    }

    private static string Quote(string? value) => value is null ? "<null>" : $"\"{value}\"";

    private static void Report(string message) =>
        TestContext.Current.TestOutputHelper?.WriteLine(message);

    private static UiaContextReader RealReader(out UiaWorker worker)
    {
        worker = UiaWorker.CreateDefault();
        return new UiaContextReader(worker, new Blocklist(), new ContextReadOptions
        {
            Deadline = TimeSpan.FromSeconds(5),
        });
    }

    private static string WriteFixturePage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jane-context-{Guid.NewGuid():N}.html");
        File.WriteAllText(
            path,
            "<!doctype html><meta charset=\"utf-8\"><title>Jane context fixture</title>" +
            "<body style=\"font:16px system-ui;padding:24px\">" +
            $"<textarea id=\"t\" autofocus rows=\"12\" cols=\"70\">{Fixture}</textarea>" +
            "<script>const t=document.getElementById('t');t.focus();t.setSelectionRange(0,t.value.length);</script>");
        return path;
    }

    private static string? FindChrome() => FirstExisting(
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        Path.Combine(LocalAppData, @"Google\Chrome\Application\chrome.exe"));

    private static string? FindVsCode() => FirstExisting(
        Path.Combine(LocalAppData, @"Programs\Microsoft VS Code\Code.exe"),
        @"C:\Program Files\Microsoft VS Code\Code.exe");

    private static string? FindWindowsTerminal() => FirstExisting(
        Path.Combine(LocalAppData, @"Microsoft\WindowsApps\wt.exe"));

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string? FirstExisting(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A temp file the OS still holds open is not a test failure.
        }
    }

    /// <summary>A temp directory that cleans itself up, for throwaway application profiles.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jane-ctx-{Guid.NewGuid():N}");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A profile directory the application still has open is not a test failure.
            }
        }
    }

    /// <summary>
    /// A real application, launched, brought to the foreground and killed afterwards.
    /// </summary>
    private sealed class RealApp : IDisposable
    {
        private readonly Process _process;
        private readonly Process? _windowOwner;

        private RealApp(Process process, Process windowOwner, nint window)
        {
            _process = process;
            _windowOwner = ReferenceEquals(process, windowOwner) ? null : windowOwner;
            Window = window;
            Target = new FocusedAppIdentity().Describe(window);
        }

        public nint Window { get; }

        public TargetWindow Target { get; }

        /// <param name="windowProcessName">
        /// For launchers that hand off to a different executable -- <c>wt.exe</c> starts
        /// <c>WindowsTerminal.exe</c> and exits, so the launched process never owns a window.
        /// </param>
        public static RealApp? Launch(string executable, string arguments, TimeSpan timeout, string? windowProcessName = null)
        {
            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false })!;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return null;
            }

            var owner = WaitForWindowOwner(process, windowProcessName, timeout);
            if (owner is null || owner.MainWindowHandle == 0)
            {
                Kill(process);
                return null;
            }

            var app = new RealApp(process, owner, owner.MainWindowHandle);
            return app.Activate() ? app : Fail(app);

            static RealApp? Fail(RealApp app)
            {
                app.Dispose();
                return null;
            }
        }

        /// <summary>Brings the window forward and confirms it actually took the foreground.</summary>
        public bool Activate()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                _ = ShowWindow(Window, ShowRestore);
                _ = SetForegroundWindow(Window);
                Thread.Sleep(250);

                if (GetForegroundWindow() == Window)
                {
                    return true;
                }
            }

            return false;
        }

        private static Process? WaitForWindowOwner(Process launched, string? windowProcessName, TimeSpan timeout)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < timeout)
            {
                if (windowProcessName is null)
                {
                    launched.Refresh();
                    if (!launched.HasExited && launched.MainWindowHandle != 0)
                    {
                        return launched;
                    }
                }
                else
                {
                    var candidate = Process.GetProcessesByName(windowProcessName)
                        .FirstOrDefault(static p => p.MainWindowHandle != 0);
                    if (candidate is not null)
                    {
                        return candidate;
                    }
                }

                Thread.Sleep(250);
            }

            return null;
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }

        public void Dispose()
        {
            if (_windowOwner is not null)
            {
                Kill(_windowOwner);
            }

            Kill(_process);
        }

        private const int ShowRestore = 9;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(nint window);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint window, int command);
    }

    /// <summary>
    /// A real top-level Win32 window with a real <c>EDIT</c> control, on its own STA thread with
    /// its own message pump -- and a switch that stops that pump answering.
    /// </summary>
    /// <remarks>
    /// The freeze is the reason this harness exists rather than a WPF one. It parks the window's
    /// thread <em>inside</em> its own window procedure, which is precisely what a busy or
    /// deadlocked application looks like to a UI Automation client: the provider is there, it just
    /// never answers. Nothing else in the test suite can produce that.
    /// </remarks>
    private sealed class Win32TextHarness : IDisposable
    {
        /// <summary>
        /// Roots every window procedure for the life of the process.
        /// </summary>
        /// <remarks>
        /// A window class holds a raw function pointer. Letting the GC collect the thunk behind it
        /// kills the process inside user32 the next time Windows dispatches a message.
        /// </remarks>
        private static readonly List<WndProc> LiveProcedures = [];

        private static int _instances;

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly WndProc _wndProc;
        private readonly bool _password;
        private readonly string _className =
            $"JaneUiaTextHost{Environment.ProcessId}_{Interlocked.Increment(ref _instances)}";

        private nint _window;
        private nint _edit;
        private volatile bool _running = true;
        private volatile bool _thawed;

        private Win32TextHarness(bool password)
        {
            _password = password;

            _wndProc = WindowProcedure;
            lock (LiveProcedures)
            {
                LiveProcedures.Add(_wndProc);
            }

            _thread = new Thread(PumpMessages) { IsBackground = true, Name = "Jane UIA harness" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public TargetWindow Target { get; private set; } = TargetWindow.None;

        public static Win32TextHarness? Create(string text, bool password = false)
        {
            var harness = new Win32TextHarness(password);
            if (!harness._ready.Wait(TimeSpan.FromSeconds(10)) || harness._window == 0 || harness._edit == 0)
            {
                harness.Dispose();
                return null;
            }

            _ = SendMessageW(harness._edit, WmSetText, 0, text);

            for (var attempt = 0; attempt < 15; attempt++)
            {
                harness.TakeForeground();
                Thread.Sleep(150);

                // Foreground is not enough. UI Automation reports the *focused element*, so until
                // the caret is actually in the edit control the read describes the frame instead,
                // which has no text provider at all -- and that reads as a product failure rather
                // than the environment problem it is.
                if (GetForegroundWindow() == harness._window && harness.HasEditFocus())
                {
                    // Captured before any freeze: GetWindowText sends WM_GETTEXT even within one
                    // process, so naming the window after it stops pumping would hang the test.
                    harness.Target = new FocusedAppIdentity().Describe(harness._window);
                    return harness;
                }
            }

            harness.Dispose();
            return null;
        }

        /// <summary>
        /// Takes the foreground, then puts the caret in the edit control.
        /// </summary>
        /// <remarks>
        /// Windows refuses <c>SetForegroundWindow</c> from a process that does not already own the
        /// foreground, which is exactly the situation a test run from a background shell is in.
        /// Attaching this thread's input queue to the current foreground thread lifts that
        /// restriction for the duration of the call -- the standard workaround, and the reason
        /// these tests run at all rather than skipping on an otherwise perfectly good desktop.
        /// The focus half is posted, because <c>SetFocus</c> only works on the owning thread.
        /// </remarks>
        public void TakeForeground()
        {
            var foreground = GetForegroundWindow();
            var foreignThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
            var thisThread = GetCurrentThreadId();
            var attached = foreignThread != 0 && foreignThread != thisThread &&
                           AttachThreadInput(thisThread, foreignThread, true);

            try
            {
                _ = ShowWindow(_window, SwShow);
                _ = BringWindowToTop(_window);
                _ = SetForegroundWindow(_window);
            }
            finally
            {
                if (attached)
                {
                    _ = AttachThreadInput(thisThread, foreignThread, false);
                }
            }

            _ = PostMessageW(_window, WmFocusEdit, 0, 0);
        }

        /// <summary>Whether the caret is genuinely in the edit control, not merely in the window.</summary>
        public bool HasEditFocus()
        {
            var thread = GetWindowThreadProcessId(_window, out _);
            if (thread == 0)
            {
                return false;
            }

            var info = new GuiThreadInfo { cbSize = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(thread, ref info) && info.hwndFocus == _edit;
        }

        public void SelectAll()
        {
            _ = SendMessageW(_edit, EmSetSel, 0, -1);
            Thread.Sleep(150);
        }

        public void PlaceCaretAt(int offset)
        {
            _ = SendMessageW(_edit, EmSetSel, offset, offset);
            Thread.Sleep(150);
        }

        /// <summary>Parks the window's thread inside its own window procedure.</summary>
        public void Freeze()
        {
            _ = PostMessageW(_window, WmFreeze, 0, 0);
            Thread.Sleep(200);
        }

        public void Thaw()
        {
            _thawed = true;
            Thread.Sleep(100);
        }

        private void PumpMessages()
        {
            var instance = GetModuleHandleW(null);

            // One class per harness, never shared. A shared class keeps the *first* registration's
            // window procedure, so a second harness in the same process would dispatch into the
            // first instance -- whose edit handle is already destroyed, so focus silently goes
            // nowhere and the read describes the window frame instead of a text control.
            var windowClass = new WndClassExW
            {
                cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = _className,
            };

            _ = RegisterClassExW(ref windowClass);

            _window = CreateWindowExW(
                0, _className, "Jane context harness", WsOverlappedWindow | WsVisible,
                140, 140, 760, 380, 0, 0, instance, 0);

            if (_window != 0)
            {
                var style = WsChild | WsVisible | EsMultiline | EsAutoVScroll | EsWantReturn;
                if (_password)
                {
                    // ES_PASSWORD is what makes the provider report IsPassword; multiline edits
                    // ignore it, so a password harness is deliberately single-line.
                    style = WsChild | WsVisible | EsPassword | EsAutoHScroll;
                }

                _edit = CreateWindowExW(WsExClientEdge, "EDIT", string.Empty, style, 0, 0, 740, 320, _window, 0, instance, 0);
            }

            _ready.Set();

            while (_running && GetMessageW(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        }

        private nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
        {
            if ((message == WmSetFocus || message == WmFocusEdit) && _edit != 0)
            {
                // SetFocus is a no-op from any thread but the one that owns the window, and
                // SetForegroundWindow on an already-foreground window raises no WM_SETFOCUS, so
                // relying on either alone leaves the caret nowhere.
                _ = SetActiveWindow(_window);
                _ = SetFocus(_edit);
                return 0;
            }

            if (message == WmFreeze)
            {
                // Thread.Sleep, not a wait handle. The CLR gives an STA thread a *pumping* wait,
                // so blocking on a ManualResetEventSlim here would keep dispatching the very
                // messages UI Automation needs and the window would not be frozen at all.
                // A hard ceiling means a mistake in a test cannot wedge the run for good.
                var ceiling = Stopwatch.StartNew();
                while (!_thawed && ceiling.Elapsed < TimeSpan.FromSeconds(60))
                {
                    Thread.Sleep(20);
                }

                return 0;
            }

            if (message == WmClose)
            {
                _running = false;
                PostQuitMessage(0);
                return 0;
            }

            return DefWindowProcW(window, message, wParam, lParam);
        }

        public void Dispose()
        {
            _thawed = true;
            _running = false;

            if (_window != 0)
            {
                _ = PostMessageW(_window, WmClose, 0, 0);
            }

            _ = _thread.Join(TimeSpan.FromSeconds(5));
            _ready.Dispose();
        }

        private const int SwShow = 5;
        private const uint WmFocusEdit = 0x8001;
        private const uint WmFreeze = 0x8002;
        private const uint WmSetFocus = 0x0007;
        private const uint WmClose = 0x0010;
        private const uint WmSetText = 0x000C;
        private const uint EmSetSel = 0x00B1;
        private const uint WsOverlappedWindow = 0x00CF0000;
        private const uint WsVisible = 0x10000000;
        private const uint WsChild = 0x40000000;
        private const uint WsExClientEdge = 0x00000200;
        private const uint EsMultiline = 0x0004;
        private const uint EsAutoVScroll = 0x0040;
        private const uint EsAutoHScroll = 0x0080;
        private const uint EsPassword = 0x0020;
        private const uint EsWantReturn = 0x1000;

        private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassExW
        {
            public uint cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public nint hwnd;
            public uint message;
            public nint wParam;
            public nint lParam;
            public uint time;
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public uint cbSize;
            public uint flags;
            public nint hwndActive;
            public nint hwndFocus;
            public nint hwndCapture;
            public nint hwndMenuOwner;
            public nint hwndMoveSize;
            public nint hwndCaret;
            public int caretLeft;
            public int caretTop;
            public int caretRight;
            public int caretBottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(nint window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll")]
        private static extern nint SetActiveWindow(nint window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WndClassExW windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMessageW(out Msg message, nint window, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint DispatchMessageW(ref Msg message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint SendMessageW(nint window, uint message, nint wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(nint window);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetFocus(nint window);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint GetModuleHandleW(string? moduleName);
    }
}
