# Jane — "No microphone" on every dictation after the first

**Mode:** regression fix on branch `field-fixes` · **Size:** S · **Workflow:** TDD, one commit per task, no AI attribution in commits.

## Symptom

The first dictation after launch works, and works better than before (the first word is no longer
clipped). Every dictation after it fails with **"No microphone. Check Settings > System > Sound >
Input."** Restarting Jane buys exactly one more good dictation.

## Diagnosis — confirmed on this machine

**1. The log.** `%LOCALAPPDATA%\Jane\logs\jane.log` shows two launches on 2026-09-08 (20:58 and
21:32), each followed by exactly one `dictation … result=ok` line and then nothing. The failed
attempts left no line at all, which is itself a bug (Task 3).

**2. The message.** That exact text is `PipelineStatus.DefaultMessageFor(NoMicrophone)`. It is
produced by one place: `DictationOrchestrator.BeginArming` catching an exception thrown by
`_audio.Arm()`, and substituting the default text because the exception's type name does not
contain "Device" (`DescribeDeviceFailure`). So `Arm()` threw, and the real exception was hidden.

**3. The mechanism.** Yesterday's Task 4 made `WasapiCapture` ready the device at launch, `Start`
it on key-down, and `Stop` it at idle release, keeping the same NAudio `WasapiRecorder` for the
next key-down. A hardware experiment on the user's own microphone (Jabra Link 380, tried with both
default-device routing and an explicit device id) shows that recorder is **single-use**:

```
start#1 ok                      samples after start#1 = 3484
stop ok                         state Stopping -> Stopped in 63 ms, no Stopped event
start#2 FAILED CoreAudioException: The audio client is already initialized.
samples gained after start#2 = 0
```

NAudio 3.0.1's `StopRecording` leaves the `IAudioClient` initialised and `StartRecording` calls
`Initialize` again unconditionally. There is no restart path.

**4. The chain.** Launch prepares a recorder → first key-down `StartRecording` (works) → dictation
ends → 8 s later `ReleaseIfIdle` → `Pause()` → `StopRecording` → next key-down → `StartStream` →
`StartRecording` throws `CoreAudioException` → propagates out of `Arm()` → `BeginArming` reports
"No microphone". Nothing resets the recorder, so every later press fails identically until Jane is
restarted, which builds a fresh recorder.

**5. Why the suite passed.** `FakeCaptureStream` models `Stop` then `Start` as restartable, so 247
Windows tests exercised a contract the real device does not honour. The code comment on
`WasapiCaptureStream.Stop` asserted that stop-then-start is "a normal, supported usage pattern";
it was an assumption, and it was wrong for this NAudio version. There is no real-hardware test of
the capture lifecycle.

Two dictations inside the 8 s idle window do **not** hit this (`_running` is still true, so
`StartStream` returns early), which is why it reads as intermittent.

## Fix

### Task 1 — Rebuild at idle release instead of stopping

**Files:** `src/Jane.Windows/Audio/WasapiCapture.cs` (`Pause`, `ReleaseIfIdle`),
`src/Jane.Windows/Audio/CaptureDevice.cs` (`ICaptureStream`),
`src/Jane.Windows/Audio/WasapiDeviceFactory.cs` (`WasapiCaptureStream.Stop`),
`tests/Jane.Windows.Tests/FakeCaptureDevice.cs`, `tests/Jane.Windows.Tests/WarmMicrophoneTests.cs`,
`tests/Jane.Windows.Tests/MicrophoneActivationTests.cs`.

The design stays: silent readying at launch, start on key-down, endpoint inactive between
dictations. What changes is how "inactive" is achieved. Idle release **disposes** the recorder
(exactly what the pre-Task-4 `Release()` did, so the headset returns to stereo and the indicator
goes out) and **immediately readies a fresh one in the background**, so the next key-down is a
first start on a never-started recorder — the path that works. The rebuild costs one device open
(100–400 ms) at the end of the idle window, never on the key-down path.

1. Red: make the fake honest. `FakeCaptureStream.Start()` throws
   `InvalidOperationException("The audio client is already initialized.")` when called after a
   `Stop()`, so the suite encodes the real constraint. Add to `WarmMicrophoneTests`: two dictations
   separated by an idle release both capture samples, `State.Error` stays null, the first stream is
   disposed, `OpenCount == 2`, and the second stream is prepared but not running before the second
   key-down.
2. Green: `Pause()` becomes `ReleaseAndReadyAsync`: `_idleRelease` cancelled → `CloseStream()` →
   `_running = false` → `_preRoll.Clear()` → publish `IsOpen = false` → `Volatile.Write(ref _armed,
   OpenGuardedAsync(start: false, rethrow: false, …))`. `Arm()` already handles `_stream == null`
   mid-prepare by going through `OpenGuardedAsync(start: true)`, which waits on `_openGate`.
3. Remove `Stop()` from `ICaptureStream`. A method whose contract ("keeps the client ready") the
   only real implementation cannot honour is how this regression got in. `Prepare()` stays: it
   still carries the ducking opt-out. `MicrophoneLatencyProbe` (which called `Stop`) disposes
   instead, and must time a **second** open+start cycle so it measures the path users actually hit.
4. Rewrite the tests that pinned the stop-and-keep contract:
   `TheGraceWindowStopsTheStreamButKeepsTheClientReady` → "the grace window releases the recorder
   and readies a fresh one"; `TheEndpointGoesInactiveOnceTheGraceWindowExpires` asserts the old
   stream **is** disposed and a new one is prepared and not running.
   `AStoppedStreamThrowsAwayItsPreRoll…` still holds (a fresh ring is empty).

### Task 2 — A start failure must never take the pipeline down

**Files:** `src/Jane.Windows/Audio/WasapiCapture.cs` (`Arm`, `StartStream`),
`src/Jane.Core/Pipeline/DictationOrchestrator.cs` (`DescribeDeviceFailure`).

Independent of Task 1 and worth having regardless: any future driver quirk should cost one open
latency, not every dictation until restart.

1. Red: fake whose first `Start()` throws once → `Arm()` does not throw, the dictation still
   captures samples after a fallback reopen, and `State.Error` ends null. Fake whose `Start()`
   always throws → `State.Error` carries the real message and the orchestrator's failure text is
   that message, not the generic one.
2. Green: `StartStream` catches, logs the exception type and message, `CloseStream()`s, and falls
   back to `OpenGuardedAsync(start: true, rethrow: false)`. `Arm()` never throws for a start
   failure. `DescribeDeviceFailure` includes `ex.Message` for any exception (the generic text was
   hiding "The audio client is already initialized", which would have named the bug in one line).

### Task 3 — Log the failures that were invisible

**Files:** `src/Jane.Core/Pipeline/DictationOrchestrator.cs` (`BeginArming`).

`BeginArming`'s catch calls `Fail` but never writes a log line, which is why yesterday's log shows
one success per launch and nothing else. Write a `dictation` line at Warning with
`result=NoMicrophone`, `exception=<type>`, `detail=<message>`. Test: a harness whose audio source
throws on `Arm()` produces exactly that line.

### Task 4 — Catch this class on real hardware

**Files:** `src/Jane.Windows/Diagnostics/MicrophoneLatencyProbe.cs`, new
`tests/Jane.Windows.Tests/RealMicrophoneTests.cs`.

1. `MicrophoneLatencyProbe` drives the same lifecycle `WasapiCapture` does — open, prepare, start,
   dispose, open again, prepare, start — and reports the second cycle's start latency. A probe that
   only ever starts once cannot see a single-use recorder.
2. A real-device regression test: open the default capture endpoint through the real
   `WasapiDeviceFactory` (`Assert.SkipWhen` there is none, following `ContextReaderTests`), build a
   `WasapiCapture` with `IdleRelease = 50 ms`, run Arm → Stop → wait past idle → Arm → Stop, and
   assert the second `CapturedAudio` has samples and `State.Error` is null. It activates the
   microphone for well under a second; say so in its remarks.

### Task 5 — Correct the record

`STUDY_GUIDE.md` "The microphone stays off, and the first word survives anyway": the honest limit
paragraph must add that NAudio's recorder is single-use, so the design is "readied at launch, and
rebuilt after every pause", not "stopped and restarted". `WasapiCaptureStream` loses the comment
claiming restart is supported.

## Verify

Reinstall (`sign-uiaccess.ps1`, then elevated `install.ps1`). Dictate three times with more than ten
seconds between them, then twice in quick succession. All five must inject, the log must show five
`result=ok` lines, and nothing may say "No microphone". Run `jane doctor` and confirm
`audio.microphone_latency` reports a second-cycle start under 50 ms.

## Out of scope

Driving `IAudioClient` directly to get a genuine stop/restart. Rebuilding is cheaper to get right
and the rebuild happens off the key-down path, which was the whole point.
