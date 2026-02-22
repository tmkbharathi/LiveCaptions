# Live Captions

A real-time system audio transcription overlay for Windows, built with .NET 8, Whisper.net, and GStreamer.

## Features

- Captures system audio (speaker loopback) in real-time via GStreamer
- Transcribes using OpenAI Whisper (`tiny.en` model) running locally on CPU
- Overlay window always on top, pinned to all virtual desktops
- 2-line subtitle display: text grows left-to-right, scrolls up when full
- Sentence segmentation via VAD silence detection (1.2 s timer)
- Translation-ready output layer (`ITranslator` hook)

---

## Architecture

The codebase is organized into 6 clean, separated layers:

```
GStreamerSource  ──►  AudioManager  ──►  WhisperEngine
      (Audio layer)        (Buffer)       (Transcription)
                                               │
                                         VadSegmenter
                                       (Segmentation)
                                               │
                                    SubtitleOutputManager
                                           (Output)
```

| Layer | Files | Responsibility |
|---|---|---|
| **Audio Source** | `Audio/IAudioResource.cs`<br>`Audio/GStreamerSource.cs` | GStreamer pipeline — captures loopback audio at 16 kHz mono S16LE |
| **Audio Buffer** | `Audio/AudioManager.cs` | Chunks PCM into 0.25 s blocks, maintains 3 s session buffer, tracks voice activity |
| **Transcription** | `Transcription/ITranscriptionEngine.cs`<br>`Transcription/WhisperEngine.cs` | Runs Whisper inference on session snapshot, returns transcript string |
| **Segmentation** | `Segmentation/ISentenceSegmenter.cs`<br>`Segmentation/VadSegmenter.cs` | Runs inference loop; fires `isFinal=true` via a 1.2 s silence timer |
| **Output** | `Output/IOutputManager.cs`<br>`Output/SubtitleOutputManager.cs` | Accumulates committed text, splits across 2 subtitle lines, scrolls on overflow |
| **Facade** | `TranscriptionService.cs` | Wires all layers together; backward-compatible constructor |

---

## Subtitle Display Behaviour

Text starts on **line 1** and grows left-to-right:

```
line 1: Hello this is a live caption test             ← grows here
line 2: (empty)
```

When line 1 fills (~72 chars), overflow spills to line 2:

```
line 1: Hello this is a live caption test running on
line 2: my computer right now                         ← continues here
```

When both lines fill, the oldest text drops off and everything scrolls up. When you pause speaking (1.2 s silence), the current text is committed and line 2 clears for the next sentence.

---

## Prerequisites

- **Windows 10/11**
- **.NET 8 SDK** — [download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **GStreamer 1.x** (MinGW 64-bit) — [download](https://gstreamer.freedesktop.org/download/)
  - Required plugins: `wasapi2src`, `audioconvert`, `audioresample`, `appsink`
  - Set environment variable: `GSTREAMER_1_0_ROOT_MINGW_X86_64` → your GStreamer install path

---

## Build & Run

```bash
# Build
dotnet build -r win-x64

# Run (downloads tiny.en model on first launch ~74 MB)
dotnet run -r win-x64
```

The model (`tiny.en.bin`) is downloaded automatically to the app directory on first run and cached for subsequent runs.

---

## Extending

### Add a new audio source
Implement `IAudioResource` (e.g. `MicSource`, `FileSource`) and pass it to `AudioManager.Attach()`.

### Add translation
Implement `Output.ITranslator` and set it on the output manager:

```csharp
outputManager.Translator = new MyTranslator(targetLanguage: "ta");
```

### Swap the transcription engine
Implement `ITranscriptionEngine` (e.g. Azure Speech, Vosk) and pass it to `VadSegmenter`.

---

## Model Choice

| Model | Size | Speed (CPU) | Accuracy |
|---|---|---|---|
| `tiny.en` | 74 MB | ~0.3–0.5 s/chunk | Good |
| `base.en` | 142 MB | ~1–3 s/chunk | Better |

`tiny.en` is recommended for real-time use. Change the model name in `Program.cs` → `EnsureModelExists("base.en")` if higher accuracy is needed.

---

## Troubleshooting

**GStreamer not found**
- Verify `GSTREAMER_1_0_ROOT_MINGW_X86_64` is set and points to the correct directory
- Run `gst-launch-1.0 wasapi2src loopback=true ! fakesink` to verify loopback works

**No audio / blank transcription**
- Ensure system audio is playing (Whisper needs sound to transcribe)
- Check Windows privacy settings: Apps → Microphone access must be ON

**High CPU usage**
- Switch to `tiny.en` (fastest model)
- On slow machines, increase `ChunkSize` in `AudioManager.cs` to reduce inference frequency
