using System;
using System.Threading.Tasks;
using LiveTranscriptionApp.Audio;
using LiveTranscriptionApp.Transcription;
using LiveTranscriptionApp.Segmentation;

namespace LiveTranscriptionApp
{
    /// <summary>
    /// Facade that wires all layers together:
    ///   GStreamerSource → AudioManager → WhisperEngine → VadSegmenter
    ///
    /// Consumers (e.g. Program.cs) still use the same constructor signature
    /// as before — no breaking change to existing UI code.
    /// </summary>
    public class TranscriptionService
    {
        // ── Layers ─────────────────────────────────────────────────────────────
        private readonly GStreamerSource _gstreamer  = new();
        private readonly AudioManager    _audio      = new();
        private readonly WhisperEngine   _whisper    = new();
        private VadSegmenter?            _segmenter;

        // ── Callbacks (for backward-compat with Program.cs) ────────────────────
        private readonly Action<string, bool> _onTranscription;
        private readonly Action<float>?       _onAudioLevel;

        public TranscriptionService(
            Action<string, bool> onTranscription,
            Action<float>?       onAudioLevel = null)
        {
            _onTranscription = onTranscription;
            _onAudioLevel    = onAudioLevel;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────
        public Task InitializeAsync(string modelPath)
        {
            // 1. Transcription engine
            _whisper.InitializeAsync(modelPath);

            // 2. Segmenter wires AudioManager + WhisperEngine
            _segmenter         = new VadSegmenter(_audio, _whisper);
            _segmenter.OnSegment += (text, isFinal) =>
                _onTranscription(text, isFinal);

            // 3. AudioManager wires into audio source
            _audio.Attach(_gstreamer);
            _audio.OnLevelChanged += level => _onAudioLevel?.Invoke(level);

            // 4. Initialize GStreamer pipeline
            _gstreamer.Initialize();

            return Task.CompletedTask;
        }

        public void Start()
        {
            _gstreamer.Start();
            _segmenter?.Start();
        }

        public void Stop()
        {
            _segmenter?.Stop();
            _gstreamer.Stop();
        }
    }
}
