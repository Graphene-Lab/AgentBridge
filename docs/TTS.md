# Text-to-speech (TTS)

AgentBridge speaks with a local neural TTS engine. **Kokoro is the engine: it works on
every machine, immediately, with nothing to install.** The engine catalog is designed to
grow — a future engine registers in `TtsEngineSupport` (AIOrchestrator) and is then
selectable through the same `/ttsengine` command and `appsettings` preference.

## Engines at a glance

| Engine | Works out of the box | Model | Speed | When to use |
|---|---|---|---|---|
| **Kokoro** (default) | **Yes — everywhere** (Windows, Linux, macOS; any CPU; no drivers, no accounts) | ~325 MB, **included in the archive** | realtime on a normal CPU | The default — the voice for the chat, the phone and the podcast tool |

The engine is selected through the same preference every time:

- **`/ttsengine`** in the terminal UI — shows the known engines and sets the engine.
- **`appsettings.json`** → `"Tts": { "Engine": "kokoro" }` (the `/ttsengine` command persists
  here). A value outside the catalog falls back to Kokoro and logs why, so a stale setting
  never breaks the TTS.

> The catalog (`TtsEngineSupport` in Graphene.AIOrchestrator) currently registers Kokoro
> only. Future engines add their name + availability gate there and inherit the selection
> path (host config, `/ttsengine`, `PODCAST_TTS_ENGINE` for the plugins) without host changes.

## Kokoro — nothing to install

- The voices and the model (`kokoro.onnx`) are **shipped inside every archive** — no download,
  no drivers, no accounts.
- Phonemization is fully managed (no espeak binaries), so it behaves identically on Windows,
  Linux and macOS, x64 and ARM64.
- The terminal, the SIP phone bridge and the podcast tool all use this engine in-process.
- **GPU acceleration** (optional): when the CUDA Toolkit 12.x + cuDNN 9.x are installed, the
  engine uses the CUDA execution provider automatically (the archive ships the provider
  DLL). Without CUDA it runs on the CPU — realtime anyway on a normal machine. No manual
  setup; the engine probes the standard install directories and sets the runtime PATH itself.

## Platform prerequisites

| Requirement | Needed for | How to install (official) |
|---|---|---|
| nothing | Kokoro (CPU) | — |
| NVIDIA driver | Kokoro GPU (CUDA, optional) | https://www.nvidia.com/drivers |
| CUDA Toolkit 12.x + cuDNN 9.x | Kokoro GPU (CUDA, optional) | https://developer.nvidia.com/cuda-downloads · https://developer.nvidia.com/cudnn |

Kokoro needs **no espeak, no native phonemizer, no audio stack**: it ships everything in
managed code and works on Windows, Linux and macOS out of the box. The `install.ps1`
one-liner detects a CUDA-capable GPU and **asks** whether to install the CUDA Toolkit +
cuDNN silently; answering no keeps everything working on the CPU, and the GPU is picked up
automatically at runtime if the toolkit is installed later.

## Troubleshooting

- **"kokoro.onnx not found"** — the model file is missing next to the executable; reinstall
  from the latest release archive (it is included).
- **A podcast records with a wrong voice** — the tool resolves the voice from the episode
  language; check the configured language (a missing voice falls back to the first loaded
  one).
