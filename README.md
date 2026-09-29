# Jane

**System-wide push-to-talk dictation for Windows, running entirely on your own machine.**

Hold a key, speak, release. The text appears in whatever window you were already using — your
editor, your browser, a chat box, a terminal. No account, no subscription, no audio leaving the
machine. Every model runs locally on the CPU.

> **Status:** a personal project, built and used daily on one machine. It is complete and it works,
> but it is not a packaged product — there is no installer, no auto-update, and no signed release.
> See [Building from source](#building-from-source).

---

## What it does

- **Push-to-talk dictation anywhere.** A global hotkey (Right Ctrl by default) captures speech and
  types the result into the focused window.
- **Punctuation and capitalisation without asking.** The speech model emits them directly; a local
  language model then removes filler, resolves spoken self-corrections ("go to the store —
  actually, the pharmacy"), and applies the structure you meant.
- **Numbers and symbols written the way they are written.** "Ticket one eight eight" types
  `ticket 188`, "three slash four" types `3/4`, and "hello comma world period" types
  `hello, world.` This is done in code on every dictation, so it holds even when the language
  model is skipped.
- **Edit Mode.** Select text, hold the key, say "make this shorter" — the selection is rewritten
  rather than replaced with the words you just said. If Jane cannot read the selection, it says so
  instead of guessing.
- **Deep Context.** Reads the focused window for proper nouns and jargon and biases recognition
  toward them, so `SettingsStore` and `Kubernetes` come out spelled correctly.
- **A custom dictionary and per-application instructions.** Different formatting for your terminal
  than for your email.
- **Transcript history**, searchable and re-injectable. Audio is never written to disk.

## Why it is interesting

The hard parts of this project are not the machine learning — the models are downloaded, not
trained. They are the operating-system problems underneath:

- **Getting text into another application reliably.** Two injection strategies (SendInput with
  Unicode, and a clipboard round-trip), chosen per application, with a modifier gate so a held Ctrl
  cannot turn dictated text into control chords in a terminal.
- **Never stealing focus.** The status pill floats over everything and must never take focus — if
  it did, the dictation would go into the pill instead of the document.
- **Bluetooth headsets.** Any open capture stream forces a headset out of stereo A2DP into
  narrowband hands-free mode, wrecking playback quality. Jane opens the microphone only while you
  are actually dictating, and says "Connecting…" rather than claiming to listen before it has a
  microphone.
- **Reading the screen without leaking secrets.** Deep Context is gated by a three-layer blocklist,
  because your bank and your Google Doc are both `chrome.exe`.
- **uiAccess.** Typing into an elevated window requires a signed binary in a secure location — see
  [Signing and uiAccess](#signing-and-uiaccess).

## How it works

```
key down ──► open mic ──► capture (WASAPI, 500 ms pre-roll ring)
                │
                ├──► UIA context read (async, 80 ms budget) ──► hotwords
                │
key up   ──► VAD trim (Silero) ──► ASR (Parakeet-TDT int8, CPU) ──► raw transcript
                │
                ├──► mode select: dictate │ edit │ edit-unavailable
                │
                └──► LLM cleanup (Qwen3 via Ollama) ──► spacing policy ──► inject at caret
```

| Project | Responsibility |
|---|---|
| `Jane.Core` | The pipeline, settings, history, vocabulary, blocklist. No Win32 — which is what makes it testable. |
| `Jane.Windows` | Audio capture, keyboard hook, text injection, UI Automation. |
| `Jane.Speech` | Parakeet and Whisper recognisers, VAD, model catalogue and downloader. |
| `Jane.Llm` | Ollama client and prompt construction. |
| `Jane.App` | WPF tray app, overlay, settings and onboarding. |
| `Jane.Bench` | `doctor`, `bench` and `eval` command-line tools. |

## Performance

Measured on one desktop CPU. `dotnet run --project src/Jane.Bench -- bench` reproduces these.

| Measurement | Result |
|---|---|
| ASR warm, 1 s utterance | **80 ms** |
| ASR warm, 10 s utterance | **546 ms** |
| Real-time factor | **0.055** (~18× faster than realtime) |
| Cold session init | **1.4 s**, paid once at startup |
| Word error rate (synthetic corpus) | **6.6%** |
| Whisper `large-v3-turbo` q5_0, same utterance | 13,568 ms — *140× slower, which is why Parakeet won* |

## Privacy

Nothing on the dictation path leaves the machine, and a test asserts that every socket the pipeline
opens is loopback. There is no telemetry, no crash reporting and no update check.

The one exception is deliberate and user-initiated: model weights are downloaded on first run from
`github.com` and `huggingface.co`, each verified against a SHA-256 pinned in the source before it
is unpacked.

Transcript history is stored in plaintext SQLite at `%LOCALAPPDATA%\Jane\jane.db` and kept until
you delete it. **Audio is never written to disk.**

## Requirements

- Windows 11 (Windows 10 will probably work; it is untested)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) 10.0.400 or later
- [Ollama](https://ollama.com) for the language-model cleanup step, or run `build/get-ollama.ps1`
  to fetch a standalone copy
- ~2 GB of disk for model weights, downloaded on first run
- No GPU required. Everything runs on the CPU by default.

## Building from source

```powershell
git clone https://github.com/Divici/Jane1.0.git
cd Jane1.0

dotnet build
dotnet run --project src/Jane.Bench -- doctor    # checks your machine, 10 probes
dotnet run --project src/Jane.App                # run it
```

On first launch, onboarding downloads the models and walks through a test dictation.

To install it properly — under `%ProgramFiles%`, starting with Windows:

```powershell
./build/publish.ps1          # self-contained single-file win-x64 build
./build/install.ps1          # requires an elevated PowerShell
```

`build/ship.ps1` does publish, sign and install in one step, for updating an existing install.

### Signing and uiAccess

Typing into an **elevated** window requires `uiAccess`, which Windows grants only to a
digitally-signed binary running from a secure location. Without it Jane works completely normally —
you simply cannot dictate into an app running as administrator.

`build/sign-uiaccess.ps1` generates a self-signed certificate and installs it into the machine's
trusted root store. **That is appropriate for your own machine and nowhere else.** A root
certificate means anything signed by that key is trusted by your whole operating system, so never
install someone else's, and do not distribute builds signed this way. Proper distribution needs a
real code-signing certificate.

## Testing

```powershell
dotnet test
```

Around 650 tests across four projects, including a real Win32 window that receives injected text, a
UI Automation harness, and an idle-footprint test that launches the app as a child process and
samples performance counters.

**Run the suite with nothing else competing for the hardware.** Parts of `Jane.Windows.Tests` need
exclusive use of the microphone, NVML and UI Automation, and they block rather than fail when
something else holds one. Two things cause that in practice: a running copy of Jane, and two test
projects running at the same time. Quit Jane first, and run the projects one at a time:

```powershell
dotnet run --project src/Jane.Bench -- doctor   # confirms the devices are free
```

Symptom, if you hit it: a test process sitting at near-zero CPU for minutes. Every class passes on
its own, so `-class Jane.Windows.Tests.<Name>` is the way through it.

## Known limitations

Kept honestly, and at greater length, in the project's design notes:

- Automatic spacing reads the character before the caret from the application when it can. Where
  an application will not say, it works from what Jane last typed there and gives up as soon as
  you type or click -- so after moving the caret by hand in such an application, Jane adds no
  space and you may need to add one yourself.
- The evaluation corpus is text-to-speech, not recorded human speech, so the absolute word error
  rate is optimistic. Every A/B comparison built on it is still sound.
- Deep Context in Chromium depends on an accessibility tree that Chrome builds lazily.
- Windows 11's Notepad is a Store app with no classic window handle, so dictation into it cannot be
  asserted in a test — only observed.
- The self-signed certificate is a real machine-wide trust change, mitigated by an EKU constraint,
  an offline key export, and deleting the private key after signing.

## Licence

[Apache License 2.0](LICENSE).

Third-party models and libraries, their licences and their attributions are listed in
[NOTICE.md](NOTICE.md). Two carry obligations rather than courtesies: **Parakeet-TDT-0.6B-v2 is
CC-BY-4.0** and **Silero VAD is MIT**, and both require attribution in the shipped product.
