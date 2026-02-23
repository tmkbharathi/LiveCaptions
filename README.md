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

```mermaid
flowchart TD
    G[GStreamerSource\nAudio layer] -->|PCM Data| A[AudioManager\nBuffer]
    A -->|30s Sliding Window| W[WhisperEngine\nTranscription]
    W -->|Raw Text| V[VadSegmenter\nSegmentation]
    A -.->|Voice Activity| V
    V -->|Final/Live Text| S[SubtitleOutputManager\nOutput]
    
    subgraph Facade
    T[TranscriptionService]
    end
    T -.-> G
    T -.-> A
    T -.-> W
    T -.-> V
    T -.-> S
```

| Layer | Files | Responsibility |
|---|---|---|
| **Audio Source** | `Audio/IAudioResource.cs`<br>`Audio/GStreamerSource.cs` | GStreamer pipeline — captures loopback audio at 16 kHz mono S16LE. |
| **Audio Buffer** | `Audio/AudioManager.cs` | Chunks PCM into 0.25 s blocks, maintains up to a 30 s safety rolling buffer (sliding window), tracks voice activity. |
| **Transcription** | `Transcription/ITranscriptionEngine.cs`<br>`Transcription/WhisperEngine.cs` | Runs Whisper inference on session snapshot, returns transcript string. |
| **Segmentation** | `Segmentation/ISentenceSegmenter.cs`<br>`Segmentation/VadSegmenter.cs` | Runs inference loop. Emits `isFinal=true` to slide the window on natural boundaries (punctuation: `.`, `?`, `!`), a 1.2 s silence pause, or a 10 s safety length limit. |
| **Output** | `Output/IOutputManager.cs`<br>`Output/SubtitleOutputManager.cs` | Accumulates committed text, splits across 2 subtitle lines, scrolls up on overflow. |
| **Facade** | `TranscriptionService.cs` | Wires all layers together; provides a unified API for the application. |

---

## Subtitle Display & Sliding Window

The captioning engine replicates the **Windows Live Captions sliding window** behaviour:

1. **Continuous Speech:** As you speak, the audio buffer grows (up to 30 seconds), allowing Whisper to retain the context of the beginning of your sentence without chopping it off prematurely.
2. **Natural Boundaries:** When you complete a sentence (detected by strong punctuation like `.`, `?`, `!`), the app instantly commits the text to the UI, clears the audio buffer, and seamlessly "slides" the window forward for the next sentence.
3. **Pauses & Safety Nets:** If you pause for 1.2 seconds, or speak continuously without punctuation for 10 seconds, the window safely slides forward to prevent high CPU latency.

Text starts on **line 1** and grows left-to-right:

```
line 1: Hello this is a live caption test             ← grows here
line 2: (empty)
```

When line 1 fills (~72 chars), overflow spills to line 2. When both lines fill, the oldest text drops off and everything scrolls up.

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
