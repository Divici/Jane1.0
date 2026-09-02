# Notices and attributions

Jane runs entirely on your machine. It bundles no third-party service, and the only network it
ever uses is the one-time, explicitly requested download of the model weights listed below.

Every component here is free software or freely licensed weights. Nothing in Jane costs money to
run, and nothing here has a licence that changes if you use it commercially.

Two of these licences carry an obligation rather than a courtesy: **Parakeet-TDT-0.6B-v2 is
CC-BY-4.0 and Silero VAD is MIT, and both require attribution in the shipped product.** This file
is that attribution, and it is mirrored in Jane's About view so it is visible without opening a
file.

---

## Speech recognition

### NVIDIA Parakeet-TDT-0.6B-v2

The model that turns your speech into text. English-specialised, and the reason Jane emits
punctuation and capitalisation without a separate restoration pass.

- **Licence:** Creative Commons Attribution 4.0 International (CC-BY-4.0)
- **Copyright:** NVIDIA Corporation
- **Source:** <https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2>
- **Build used:** the int8 ONNX export packaged for sherpa-onnx by the k2-fsa project,
  `sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8`
- **Changes:** none. The weights are used as published; Jane only chooses the int8 export and the
  CPU execution provider.

CC-BY-4.0 full text: <https://creativecommons.org/licenses/by/4.0/legalcode>

### Silero VAD

Detects whether you actually spoke, and trims the silence either side.

- **Licence:** MIT
- **Copyright:** Silero Team
- **Source:** <https://github.com/snakers4/silero-vad>

### OpenAI Whisper `large-v3-turbo` — second engine

Benchmarked against Parakeet on every machine and available as a fallback. On this hardware
Parakeet won by a wide margin, so Whisper is present but not selected.

- **Licence:** MIT
- **Copyright:** OpenAI
- **Source:** <https://huggingface.co/openai/whisper-large-v3-turbo>
- **Build used:** the GGML quantisations published by Georgi Gerganov at
  <https://huggingface.co/ggerganov/whisper.cpp>

---

## Language model

### Qwen3

Cleans the raw transcript: removes filler, resolves spoken self-corrections, and applies the
structure you meant.

- **Licence:** Apache License 2.0
- **Copyright:** Alibaba Cloud / Qwen Team
- **Source:** <https://huggingface.co/Qwen>
- **Builds used:** `qwen3:4b-instruct` (Qwen3-4B-Instruct-2507) on the GPU route and `qwen3:1.7b`
  on the CPU route, both served through Ollama.
- **Changes:** Jane derives `jane-qwen3-4b` and `jane-qwen3-1.7b` from these tags, changing only
  sampling parameters. The weights are unmodified.

---

## Runtimes and libraries

| Component | Licence | Source |
|---|---|---|
| sherpa-onnx | Apache-2.0 | <https://github.com/k2-fsa/sherpa-onnx> |
| ONNX Runtime | MIT | <https://github.com/microsoft/onnxruntime> |
| Whisper.net | MIT | <https://github.com/sandrohanea/whisper.net> |
| whisper.cpp | MIT | <https://github.com/ggml-org/whisper.cpp> |
| Ollama | MIT | <https://github.com/ollama/ollama> |
| llama.cpp | MIT | <https://github.com/ggml-org/llama.cpp> |
| NAudio | MIT | <https://github.com/naudio/NAudio> |
| H.NotifyIcon | MIT | <https://github.com/HavenDV/H.NotifyIcon> |
| SharpZipLib | MIT | <https://github.com/icsharpcode/SharpZipLib> |
| Microsoft.Data.Sqlite | MIT | <https://github.com/dotnet/efcore> |
| SQLite | Public domain | <https://sqlite.org/copyright.html> |
| .NET / WPF | MIT | <https://github.com/dotnet/wpf> |
| xUnit.net | Apache-2.0 | <https://github.com/xunit/xunit> |

---

## What Jane sends, and where

Nothing, on the dictation path. No telemetry, no crash reporting, no update check. Every model
runs on this machine, and a test asserts that every socket the dictation pipeline opens is
loopback.

The one exception is deliberate and user-initiated: downloading the model weights above, from
`github.com` and `huggingface.co`, the first time you ask for them. Each download is verified
against a SHA-256 pinned in the source before it is unpacked.

## What Jane keeps

Transcript history is stored in plaintext SQLite at `%LOCALAPPDATA%\Jane\jane.db` and is retained
until you delete it. **Audio is never written to disk.** History can be searched, individually
deleted, or erased entirely from Jane's settings.
