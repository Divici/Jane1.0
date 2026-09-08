# Jane — field fixes: onboarding replay, "Not downloaded" models, dots in Notepad, clipped first words

**Mode:** bugfix batch on the installed build (`C:\Program Files\Jane\Jane.exe`, autostarted from HKCU\...\Run) · **Size:** M · **Workflow:** TDD per task, one commit per task, no AI attribution in commits.

## Summary

Five things were reported after a day of real use. Three have a confirmed root cause in the code plus evidence on the machine; two need a short diagnosis step before the fix is certain, and the plan includes the fix that follows from each plausible cause.

| # | Symptom | Status | Root cause |
|---|---------|--------|------------|
| 1 | Onboarding runs on every boot | **Confirmed** | `App` gates onboarding on the stale `settings.json`, but onboarding writes the flag to SQLite |
| 2 | Models page says Qwen "Not downloaded" | **Confirmed** | Installed build has no `tools\ollama`, so Jane's private Ollama never starts and every presence probe returns false |
| 3 | Long dictation into Notepad produced dots; typing afterwards produced more dots | **Diagnose first** | Only >200-char text goes down the clipboard path; the paste is restored after a 60 ms fixed delay. Terminals (where Claude Code runs) never use that path, which matches "works here, nowhere else" |
| 4 | First words of every dictation cut off | **Confirmed mechanism** | Default microphone activation is `WhileDictating`: the device is *opened* on key-down, so the 500 ms pre-roll ring is empty when the first word is spoken. The mic must stay off until key-down (user constraint), so the fix is a pre-initialized but stopped stream, not an always-open one |
| 5 | "Does Claude Code have its own dictation on Right Ctrl?" | Answered below | see § Claude Code and Right Ctrl |

Evidence gathered (no code changed):

- `%LOCALAPPDATA%\Jane\settings.json` last written 2026-09-02; `jane.db` contains `onboardingComplete = true`, `hotkey.mode = 0` (Hold), `speech.engineId = parakeet-tdt-0.6b-v2-int8`.
- `C:\Program Files\Jane\` contains `Jane.exe`, `NOTICE.md`, `runtimes\`, `ggml-metal.metal` — **no `tools\ollama`**. Repo checkout has `tools\ollama\ollama.exe`. `build/publish.ps1` bundles it only with `-IncludeOllama`, which `ship.ps1` defaults off.
- Nothing is listening on 127.0.0.1:11435 (Jane's Ollama) or 11434 (a system Ollama).
- No log directory exists under `%LOCALAPPDATA%\Jane\logs` even though `JanePaths.Logs` points there and the startup error text says "See the log for details". Jane has no file logger today.
- History rows in `jane.db` contain no runs of dots, so the dots were not Jane's `FinalText`; they were produced between injection and Notepad.

---

## Task 1 — Onboarding must never replay once finished

**Files:** `src/Jane.App/Composition/JaneHost.cs` (`Settings` property, line ~278), `src/Jane.App/App.xaml.cs:99`, `src/Jane.App/Composition/WindowLauncher.cs:79`, tests in `tests/Jane.App.Tests/OnboardingTests.cs`.

**Cause.** `JaneHost.Settings => _settings.Current` where `_settings` is the JSON `SettingsStore`. Since settings moved into the database (`SettingsRepository`, exposed as `Settings2`), the JSON file is only a bench-result side channel and is never rewritten with `OnboardingComplete`. `FirstRunViewModel.FinishAsync` persists through `Settings2`. On the next launch `App.StartHostAsync` reads the JSON copy, sees `false`, and opens the wizard again. `WindowLauncher.RunOnboardingAsync` reports completion from the same stale source.

**Fix.**
1. Red: test in `OnboardingTests` — open a host against a temp `JANE_HOME`, complete onboarding via the view model, dispose, recreate the host, assert `host.Settings.OnboardingComplete` is true **and** that `App`'s gate (extract the condition into a pure `StartupPolicy.ShouldRunOnboarding(JaneSettings, IReadOnlyDictionary<string,string?> env)` so it is testable without WPF) returns false.
2. Green: make `JaneHost.Settings` return `Settings2.Current`. Audit every other `host.Settings.` read in `Jane.App` (hotkey binding at line 227 already uses `Settings2`) and route them to the repository. Keep the JSON store solely for `ReadStartupSettings`' bench import; rename the field to `_benchFile` so the two cannot be confused again.
3. Guard: a startup self-heal — if the repository says complete but the wizard is about to open, log and skip. Cheap insurance against a second regression.
4. Also honour a completed onboarding when the database is present but the *user* chose to skip a non-required step (LLM rows are `required: false`); confirm the wizard's `Finish` is reachable without Qwen pulled, since Task 2 means LLM pulls have never succeeded on this machine.

**Verify:** run the app tests, then reboot-equivalent check: kill Jane, relaunch `C:\Program Files\Jane\Jane.exe`, no wizard.

---

## Task 2 — LLM models show "Not downloaded" because the installed build has no Ollama

**Files:** `build/publish.ps1`, `build/ship.ps1`, `build/install.ps1`, `src/Jane.App/Composition/JaneHost.cs` (`RepoOrInstallRoot`, `Create`), `src/Jane.App/Composition/LlmStack.cs`, `src/Jane.Llm/OllamaSupervisor.cs:69`, `src/Jane.App/Settings/ModelRow.cs`, `src/Jane.App/Settings/ModelProvisioning.cs`.

**Cause.** `RepoOrInstallRoot()` walks up from `AppContext.BaseDirectory` looking for `tools\ollama`; in `C:\Program Files\Jane` it finds nothing and falls back to the base directory. `OllamaSupervisor` sees the exe is missing and the stack never starts. `ModelRow.IsInstalled` is computed from `IsPulledAsync`, which asks `/api/tags` on a server that is not running, so every Qwen row reads "Not downloaded" and every "Download" click fails with a message that blames the model rather than the missing runtime. Onboarding's model step has the same rows, which is also why the wizard could never show Qwen as ready.

**Fix — runtime resolution (code).**
1. Red: unit tests for a new `OllamaLocator` (in `Jane.Llm`) that resolves the exe in order: `JANE_OLLAMA_EXE` env override → `<install root>\tools\ollama\ollama.exe` → `%LOCALAPPDATA%\Jane\tools\ollama\ollama.exe` → `ollama` on `PATH` / `%LOCALAPPDATA%\Programs\Ollama\ollama.exe` (the official installer's location). Return a typed result: `Found(path)` or `Missing(searched paths)`.
2. Green: `JaneHost.Create` uses the locator. When `Missing`, still build the settings/onboarding rows, but the LLM rows show a distinct state **"Model host not installed"** with a one-click **"Install model host"** action that runs the same download `build/get-ollama.ps1` does (standalone zip → `%LOCALAPPDATA%\Jane\tools\ollama`, with progress and checksum). After install, the stack starts without restarting Jane, and the rows re-probe.
3. `ModelRow` must distinguish three states instead of two: `Installed`, `Not downloaded`, `Unavailable (reason)`. A probe *exception* today collapses to "Not downloaded" (`ModelRow.cs:212`); it must surface the reason instead. Tests for each state's `StatusText`/`StatusSeverity`.
4. Also detect a **system** Ollama on 11434 as a valid host (opt-in setting `Llm.UseSystemOllama`), because a user who already has Ollama installed should not need a second 1.4 GB copy.

**Fix — packaging (scripts).**
5. `ship.ps1`/`publish.ps1`: default `-IncludeOllama` to **on** for the installer artifact, or have `install.ps1` fetch the runtime into `%LOCALAPPDATA%\Jane\tools\ollama` on first install. Pick the second: it keeps the installer small and matches the in-app installer from step 2. Add a `BuildScriptTests` case asserting the installed tree contains either `tools\ollama` or the first-run fetch hook.
6. `DoctorCommand` gains a probe: "Ollama runtime present at …" so `jane doctor` catches this class of problem.

**Verify:** on this machine, after installing the runtime through the new UI, Settings → Models shows Qwen 4B and 1.7B as Installed after the pull; a dictation in Notepad is formatted (history row has `LlmModel` set).

---

## Task 3 — Dots in Notepad after a long dictation, then dots on every keystroke

**Files:** `src/Jane.Windows/Injection/ClipboardInjector.cs`, `InjectionStrategySelector.cs`, `SendInputInjector.cs`, `ModifierGate.cs`, `src/Jane.Windows/Hotkeys/LowLevelKeyboardHook.cs`, `src/Jane.Core/Pipeline/DictationOrchestrator.cs`, new `src/Jane.Core/Diagnostics/FileLog.cs`.

**What is known.** Text ≤200 chars is typed with `KEYEVENTF_UNICODE` everywhere; text >200 chars goes to the clipboard strategy in every app except terminals (`TerminalReason`, limit `int.MaxValue`). Claude Code runs in a terminal, so it only ever sees the Unicode path — that is the "works here, nowhere else" line. The clipboard path is: snapshot clipboard → set payload → send Ctrl+V chord → wait **60 ms** → restore the snapshot. History shows Jane's own text contained no dots.

**Step 0 — make it diagnosable (this is a deliverable, not a chore).** Jane has no log file. Add a rolling file log at `JanePaths.Logs` (the path already exists in code, the directory is never created) with one structured line per dictation: target class/process, strategy chosen, chars, `SendInput` accepted/total, modifier-gate result (`InitiallyHeld`/`StillHeld`), paste-settle wait, clipboard restore result, ASR duration and audio length. Tray menu gets "Open log folder". Startup failures already promise "See the log for details"; make that true.

**Step 1 — reproduce** with the log on: dictate ≥60 s into Windows 11 Notepad (WinUI), then into classic `notepad.exe` behaviour equivalents (WordPad is gone; use Sticky Notes / Word / a `RichEdit` sample), then type manually afterwards. Record which app shows dots and whether `GetAsyncKeyState(VK_CONTROL/VK_RCONTROL)` reads held afterwards (log it on the next key event through the hook).

**Step 2 — fixes, ordered by likelihood.**
- **Clipboard restore race.** Restoring 60 ms after Ctrl+V is a guess; Windows 11 Notepad is a slow WinUI app and can read the clipboard *after* the restore, pasting whatever was there before (or nothing). Replace the fixed delay with a settle protocol: register as clipboard viewer / poll `GetClipboardSequenceNumber` and only restore after either (a) the target's caret/text changed by the pasted length (UIA `TextPattern` on the focused element, which `UiaContextReader` already reaches), or (b) a ceiling of 750 ms elapses, and log which branch fired. Tests with a fake clipboard + fake target that consumes late.
- **Stuck modifier from the paste chord.** `SendPaste` throws on partial acceptance *before* the key-up records are guaranteed sent; a Ctrl-down with no Ctrl-up leaves the system with a held Ctrl, which turns every later keystroke into a control chord. Make the chord release unconditional (`finally` that sends `VK_CONTROL`/`VK_V` up), and after any injection assert via `IAsyncKeyState` that no modifier Jane touched is still down; if one is, send its up and log it. Regression test: fake `SendInput` that accepts the down records and rejects the rest must still see the ups.
- **Bound-key swallow asymmetry.** The hook swallows Right Ctrl down/up for Jane. Under `HotkeyOptions.MaxDuration` (5 min) or a watchdog re-hook mid-hold, a down can pass while the matching up is swallowed (or vice-versa), leaving apps with a phantom held Ctrl — same keystroke symptom. Make `RecordHookEvent` track "did we swallow this key's down"; only swallow the up if the down was swallowed, and never swallow an up whose down went through. Unit tests on `HotkeyStateMachine`/hook decision function for: hook reinstall mid-hold, MaxDuration expiry mid-hold, and a physical up arriving after a forced stop.
- **Notepad should not need the clipboard at all.** Add a process rule for `Notepad.exe` (WinUI Notepad's process name) → Unicode with a high ceiling, matching the terminal rule; batched Unicode injection at 40 chars/batch is fast enough for a paragraph. Keep the clipboard path for Electron/Chromium/Office where Unicode is known to break.

**Step 3 — long-dictation robustness (adjacent, same session).** `MaxCaptureDuration` is 5 min and the whole buffer goes to `ParakeetRecognizer` in one call. Sherpa-onnx offline recognisers degrade on multi-minute inputs and the documented approach is VAD-segmented recognition. In `DictationOrchestrator`, when the utterance exceeds ~30 s, split on the Silero VAD's silence boundaries and recognise per segment, joining with `SpacingPolicy`; word timings stay per segment. Tests: a synthetic 2-minute fixture built by concatenating existing `tests/fixtures/audio` clips with silence must transcribe to the concatenation of their manifest transcripts within the existing WER threshold.

**Verify:** the Step 1 reproduction produces the full text in Notepad and manual typing afterwards is normal; the log shows strategy, gate state, and settle branch for that run.

---

## Task 4 — First words are cut off

**Files:** `src/Jane.Windows/Audio/WasapiCapture.cs`, `src/Jane.Windows/Audio/PreRollBuffer.cs`, `src/Jane.Core/Settings/JaneSettings.cs` (`MicrophoneSettings`), `src/Jane.Core/Pipeline/DictationOrchestrator.cs` (`BeginArming`), `src/Jane.App/Onboarding/FirstRunViewModel.cs` (microphone step copy), `src/Jane.App/Settings/SettingsViewModel.cs`.

**Cause.** `MicrophoneSettings.Activation` defaults to `WhileDictating` with an 8 s idle release. On key-down the orchestrator calls `_audio.Arm()`, which *opens the device*. WASAPI open + first buffer is 100–400 ms depending on the driver, and the 500 ms `PreRollBuffer` only fills while the stream is running, so on a cold start there is nothing to back-date into. `Pressed` is emitted immediately (not after `MinimumHold`), so the hotkey is not the delay; the microphone is. Everyone speaks within ~150 ms of pressing, so the first syllable or word is gone on every cold dictation, and intermittently on warm ones when the release timer already fired.

**Constraint (from the user, 2026-09-08).** The microphone must stay **off until the key is pressed**. An always-open capture stream degrades the sound of everything else on the system (Bluetooth headsets drop from A2DP to the low-quality hands-free profile while a capture stream is running, and Windows ducks other apps for communications streams). `AlwaysOpen` therefore does **not** become the default; `WhileDictating` stays. The fix has to remove the open latency without keeping the stream running.

**Fix.**
1. Red: `CaptureTests` with `FakeCaptureDevice` reporting a 300 ms *open* latency and a ~5 ms *start* latency — assert that under `WhileDictating`, `Arm()` followed by speech at t=100 ms retains that speech.
2. Green — **warm but silent**: split "open" from "start". Today `Start()` maps to NAudio's `StartRecording()`, which initializes the WASAPI client *and* starts the stream in one call, so the whole cost lands on key-down. Instead, at launch (and after every device change / idle release) create and initialize the `IAudioClient` for the chosen endpoint but do **not** call `Start`. An initialized-but-stopped stream does not light the mic-in-use indicator, does not switch a Bluetooth headset to hands-free, and does not trigger ducking — the endpoint only goes active on `Start`. On key-down, `Start()` alone runs in single-digit milliseconds, well inside the 50 ms key-down-to-armed budget, so the first word is captured. On idle release call `Stop()` (endpoint inactive again) but keep the initialized client. `CaptureDevice` gets `Prepare()` / `Start()` / `Stop()` as separate members; `WasapiDeviceFactory` implements them on NAudio's `WasapiCapture` (its `Initialize` is internal to `StartRecording`, so either call the split via reflection-free subclassing or drive `IAudioClient` directly — decide by reading NAudio 2.x source during the task; direct `IAudioClient` is preferred because `Jane.Windows` already hand-writes COM for UIA).
3. **Measure, don't assume**: add a `MicrophoneProbe` check to `jane doctor` that reports, for the selected device, (a) whether the headset's playback quality changes while a stopped-but-initialized client exists (poll the render endpoint's mix format), and (b) the measured `Start()` latency. If some driver still goes active on `Initialize`, fall back for that device to the current behaviour plus a longer idle window, and say so in Settings. Test the probe against `FakeCaptureDevice`.
4. Opt out of communications ducking explicitly (`IAudioSessionControl2::SetDuckingPreference(true)`, and use the default/console stream category, never `Communications`) so even the brief dictating window does not turn other apps down. This addresses part of "messes up the sound of everything else" independently of the profile switch.
5. Grow `PreRollBuffer.DefaultWindow` from 500 ms to 1 000 ms; with a warm client the ring fills from the first `Start()`, and while the stream is stopped it simply holds the last second from the previous dictation, which must be **cleared on `Start()`** so stale audio is never back-dated in. Test that.
6. Fix the VAD leading edge: when the gate first flags speech, include the preceding 300 ms of samples (Silero has onset latency of one or two 32 ms frames plus its threshold). Test on `tests/fixtures/audio/proper-01.wav` trimmed so speech starts at 0 ms — the transcript's first word must survive.
7. Keep the idle release at 8 s (the user wants the endpoint inactive quickly); it now only controls `Stop()`, not teardown of the client.
8. Surface it: the microphone step in onboarding and the Settings microphone card say "Off until you press the key (default). Jane keeps the device ready so the first word is not lost." and show the measured start latency from the last dictation (log from Task 3 Step 0 feeds it). `AlwaysOpen` remains an option, labelled with the headset/ducking cost.

**Verify:** with a Bluetooth headset playing music, Jane idle must not change playback quality or light the mic indicator; ten cold dictations starting with "Testing one two three" all begin with "Testing" in history.

---

## Claude Code and Right Ctrl

Claude Code does have built-in voice dictation, configured with `/voice`, but its default key is **Space** (hold or tap in the chat input), not Right Control. The docs list no Right Ctrl binding. So Jane's Right Ctrl does not collide with Claude Code, and the reason dictation "works here" is the injection strategy (terminals always get the Unicode path), not a Claude feature. Nothing in `~/.claude/settings.json` or `~/.claude/keybindings.json` on this machine enables voice.

If Claude Code's Space dictation ever gets in the way, disable it in `~/.claude/keybindings.json`:

```json
{ "bindings": [ { "context": "Chat", "bindings": { "space": null } } ] }
```

Sources: https://code.claude.com/docs/en/voice-dictation.md and https://code.claude.com/docs/en/keybindings.md (Voice actions table).

---

## Status — implemented 2026-09-08

All four tasks are implemented, on branch `field-fixes`, one commit each. Suites: Core 297,
Windows 247, App 197, Speech 51, Llm 39 — all passing, `dotnet format --verify-no-changes` clean.

| Task | State |
|------|-------|
| 3 step 0 — file log | Done. `FileLog` in `Jane.Core`, one structured line per dictation, tray "Open log folder", startup failures logged |
| 1 — onboarding replay | Done. `JaneHost` no longer holds a `SettingsStore`; `StartupPolicy` is tested; a structural test keeps the field deleted |
| 2 — Ollama runtime | Done. `OllamaLocator`, in-app `OllamaRuntimeInstaller`, three-state `ModelAvailability`, runtime as its own model row, `install.ps1` fetch, doctor probe, opt-in system Ollama |
| 4 — clipped first word | Done. `Prepare`/`Start`/`Stop` split, warm-but-stopped client, ducking opt-out, 1 s pre-roll cleared on pause, 300 ms VAD leading pad, latency probe |
| 3 steps 2–3 — Notepad dots | Fixes done. Guaranteed chord release plus post-injection sweep, evidence-based clipboard settle, Notepad on the Unicode path, symmetric Escape swallowing, VAD-segmented long audio |

**Not done, and why.**

- **Task 3 step 1, the manual reproduction.** It needs a person dictating into Notepad for a minute
  with the new log running. Every fix in step 2 stands on its own and is independently tested, but
  which one actually produced the dots is still unconfirmed. The log now records strategy, modifier
  state at both ends, records accepted over records sent, and the settle branch, so one more report
  answers it.
- **One correction to the plan's diagnosis.** The bound key is never swallowed by the hook — only
  Escape is, and only while a dictation is live. The asymmetry was real but in Escape: the down was
  hidden and the up delivered. Fixed as described; the bound key is untouched and a test pins that.
- **Step 6, the install.** `build/publish.ps1` runs clean and the artifact is in `artifacts/publish`.
  `install.ps1` requires elevation, stops the running Jane, and now downloads the 1.4 GB model
  runtime, so it is left for the user to run.

## Order and dependencies

1. Task 3 Step 0 (file log) first — every other task's verification reads it.
2. Task 1 (onboarding) — smallest, highest annoyance, independent.
3. Task 2 (Ollama locator + in-app runtime install + packaging) — unblocks formatting on this machine.
4. Task 4 (warm-but-stopped microphone client + ducking opt-out + pre-roll + VAD leading edge).
5. Task 3 Steps 1–3 (reproduce, then clipboard settle, chord release, hook symmetry, Notepad rule, long-audio segmentation).
6. Ship: `build/ship.ps1`, reinstall to `C:\Program Files\Jane`, reboot, confirm no wizard, models Installed, Notepad long dictation, first-word check.

Each task ends with its tests green, `STUDY_GUIDE.md` updated (Key Decisions: settings source of truth; Ollama resolution; clipboard settle protocol; always-open microphone), and one commit.

## Out of scope

- Any change to the ASR engine choice or LLM prompts.
- Signing/UIAccess changes — the hook and injection already reach Notepad, so elevation is not the failure here.
