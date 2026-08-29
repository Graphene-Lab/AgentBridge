# Text-to-speech (TTS)

AgentBridge speaks with two local neural TTS engines. **Kokoro is the default and works on
every machine, immediately, with nothing to install.** Qwen3-TTS is an optional second engine
for machines powerful enough to run it well.

## Engines at a glance

| Engine | Works out of the box | Model | Speed | When to use |
|---|---|---|---|---|
| **Kokoro** (default) | **Yes — everywhere** (Windows, Linux, macOS; any CPU; no drivers, no accounts) | ~325 MB, **included in the archive** | realtime on a normal CPU | Default. The voice for the chat, the phone and the podcast tool |
| **Qwen3-TTS** (optional) | Only on **NVIDIA GPUs with ≥16 GB VRAM** in release builds | ~5.5 GB, **auto-downloaded on first use** | needs a powerful GPU (very slow on CPU) | Higher quality / more natural voices, Italian included |

The two engines are selected with the same preference:

- **`/ttsengine`** in the terminal UI — shows what the current machine supports and sets the engine.
- **`appsettings.json`** → `"Tts": { "Engine": "kokoro" | "qwen" }` (the `/ttsengine` command persists here).

> **Release builds gate Qwen3-TTS to NVIDIA GPUs with ≥16 GB VRAM.** On CPU the engine
> synthesizes at ~0.1× realtime: a 30-minute podcast would occupy a normal machine for
> hours. To avoid blocking a user's PC, release builds simply don't offer Qwen when the
> required GPU is absent (the app says why and stays on Kokoro). **Debug builds always allow
> Qwen** so developers can test the engine on any machine. The check reads the running app's
> build configuration and probes `nvidia-smi` for the GPU memory.

## Kokoro — nothing to install

- The voices and the model (`kokoro.onnx`) are **shipped inside every archive** — no download,
  no drivers, no accounts.
- Phonemization is fully managed (no espeak binaries), so it behaves identically on Windows,
  Linux and macOS, x64 and ARM64.
- The terminal, the SIP phone bridge and the podcast tool all use this engine in-process.

## Qwen3-TTS — what a powerful machine needs

1. **NVIDIA GPU with ≥16 GB VRAM** (release builds enforce this; the CUDA execution provider
   is used automatically).
2. The model (**~5.5 GB**) is downloaded automatically from HuggingFace on the first use into
   `%LOCALAPPDATA%\ElBruno\QwenTTS` (Windows) / `~/.local/share/ElBruno/QwenTTS` (Linux).
   Only the first run pays for it.
3. **GPU acceleration** (optional but recommended): the CUDA execution provider needs the
   NVIDIA driver plus the CUDA Toolkit and cuDNN. The engines probe the standard install
   directories and set the runtime PATH themselves — no manual configuration. Without the
   toolkit, Qwen still runs on the CPU (slow).

> The model cache can be deleted to force a re-download. The engine falls back to Kokoro
> automatically if Qwen is unavailable or fails mid-task — a podcast never stops because of
> the TTS engine.

## Platform prerequisites

### Windows

| Requirement | Needed for | How to install (official) |
|---|---|---|
| nothing | Kokoro, Qwen on CPU | — |
| NVIDIA driver | Qwen GPU (CUDA) | https://www.nvidia.com/drivers |
| CUDA Toolkit 12.x + cuDNN 9.x | Qwen GPU (CUDA) | https://developer.nvidia.com/cuda-downloads · https://developer.nvidia.com/cudnn |
| DirectX 12 | any GPU fallback | included with Windows 10/11 |

The `install.ps1` one-liner detects a CUDA-capable GPU and **asks** whether to install the
CUDA Toolkit + cuDNN silently; answering no keeps everything working on the CPU, and the GPU
is picked up automatically at runtime if the toolkit is installed later.

### Linux

| Requirement | Needed for | How to install (official) |
|---|---|---|
| nothing | Kokoro, Qwen on CPU | — |
| NVIDIA driver | Qwen GPU (CUDA) | https://www.nvidia.com/drivers (or the distro packages, e.g. `ubuntu-drivers install`) |
| CUDA Toolkit 12.x + cuDNN 9.x | Qwen GPU (CUDA) | https://developer.nvidia.com/cuda-downloads (the `.run` installer) · https://developer.nvidia.com/cudnn |

Kokoro needs **no espeak, no native phonemizer, no audio stack**: it ships everything in
managed code. Qwen on CPU works with no driver at all. On Linux the CUDA runtime libraries
are found through the standard loader (`ldconfig`) — nothing to set by hand.

### macOS

Kokoro works out of the box. Qwen3-TTS has no CUDA path on macOS (NVIDIA GPUs are not
supported by current macOS), so the Qwen engine is not available there — Kokoro covers all
TTS.

## Troubleshooting

- **`/ttsengine` says Qwen is not supported** — the machine has no NVIDIA GPU with ≥16 GB
  VRAM (or no NVIDIA driver / no `nvidia-smi`). This is by design in release builds; the
  engine stays on Kokoro, which needs nothing.
- **"kokoro.onnx not found"** — the model file is missing next to the executable; reinstall
  from the latest release archive (it is included).
- **The first Qwen podcast seems stuck on "downloading"** — the ~5.5 GB model is being
  fetched; the progress is written to the log. Interrupting and retrying resumes the download.
- **A podcast records with Kokoro even though Qwen was selected** — Qwen was unavailable
  (model download failed, GPU gate, or a synthesis error): the log contains the reason; the
  fallback is deliberate so the episode is never lost.
