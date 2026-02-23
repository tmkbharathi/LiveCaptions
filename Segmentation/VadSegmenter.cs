using System;
using System.Threading;
using System.Threading.Tasks;
using LiveTranscriptionApp.Audio;
using LiveTranscriptionApp.Transcription;

namespace LiveTranscriptionApp.Segmentation
{
    /// <summary>
    /// Voice-Activity-Detection based sentence segmenter.
    ///
    /// - Runs an async inference loop: dequeue audio chunk → run Whisper → emit partial
    /// - A System.Threading.Timer fires 1.2 s after the last detected voice activity
    ///   and emits isFinal=true to signal a sentence boundary — completely independent
    ///   of the Whisper processing loop, so commits always fire even when Whisper is busy.
    /// - Session buffer is cleared after each commit and after 3 s of continuous silence.
    /// </summary>
    public class VadSegmenter : ISentenceSegmenter
    {
        public event Action<string, bool>? OnSegment;

        private readonly AudioManager         _audio;
        private readonly WhisperEngine        _whisper;
        private readonly int                  _silenceMs;
        private readonly int                  _inferenceIntervalMs;

        private volatile string _lastPartialText = "";
        private volatile bool   _committed       = true;
        private Timer?          _silenceTimer;
        private CancellationTokenSource? _cts;

        // Throttle: don't invoke Whisper more often than once per interval.
        private DateTime _lastInferenceTime = DateTime.MinValue;

        // Minimum 0.5 s (2 chunks) of audio before first inference.
        private const int MinSessionChunks = 2;

        /// <param name="audio">AudioManager to consume chunks from.</param>
        /// <param name="whisper">Whisper engine for inference.</param>
        /// <param name="silenceMs">Milliseconds of silence before sentence commit (default 1200).</param>
        /// <param name="inferenceIntervalMs">Minimum milliseconds between successive Whisper calls (default 1000).</param>
        public VadSegmenter(AudioManager audio, WhisperEngine whisper, int silenceMs = 1200, int inferenceIntervalMs = 1000)
        {
            _audio               = audio;
            _whisper             = whisper;
            _silenceMs           = silenceMs;
            _inferenceIntervalMs = inferenceIntervalMs;

            // Wire silence timer — starts in suspended state
            _silenceTimer = new Timer(OnSilenceTimerFired,
                                      null,
                                      Timeout.Infinite,
                                      Timeout.Infinite);

            // Reset timer every time voice activity is detected
            _audio.OnLevelChanged += level =>
            {
                if (level > 0.05f)
                    _silenceTimer?.Change(_silenceMs, Timeout.Infinite);
            };
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => InferenceLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _silenceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // ── Silence timer callback ─────────────────────────────────────────────

        private void OnSilenceTimerFired(object? _)
        {
            if (!_committed && !string.IsNullOrEmpty(_lastPartialText))
            {
                OnSegment?.Invoke(_lastPartialText, true);  // isFinal = true
                _lastPartialText = "";
                _committed       = true;
                _audio.ClearSession();
            }
        }

        // ── Inference loop ─────────────────────────────────────────────────────

        private async Task InferenceLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await _audio.WaitAsync();

                // While Whisper is busy, drain the queue into the session buffer
                if (_whisper.IsBusy)
                {
                    _audio.DrainQueue();
                    continue;
                }

                if (!_audio.TryConsumeChunk()) continue;

                // Need at least 0.5 s (MinSessionChunks) before first inference
                if (_audio.SessionByteCount < MinSessionChunks * AudioManager.ChunkSize) continue;

                // Throttle: skip if we fired Whisper too recently
                var now = DateTime.UtcNow;
                if ((now - _lastInferenceTime).TotalMilliseconds < _inferenceIntervalMs) continue;

                // Transcribe fixed-size rolling session snapshot
                var snapshot = _audio.GetSessionSnapshot();
                string text  = await _whisper.TranscribeAsync(snapshot);
                _lastInferenceTime = DateTime.UtcNow;
                text         = text.Trim();

                // Filter noise/hallucinations
                if (!string.IsNullOrEmpty(text)
                    && !text.StartsWith("[")
                    && !text.StartsWith("("))
                {
                    _lastPartialText = text;

                    // Sliding Window: Natural Sentence Boundaries or Max Length
                    bool hasPunctuation = text.EndsWith(".") || text.EndsWith("?") || text.EndsWith("!");
                    bool reachedMaxLength = _audio.SessionByteCount >= (40 * AudioManager.ChunkSize); // 10 seconds

                    if (hasPunctuation || reachedMaxLength)
                    {
                        OnSegment?.Invoke(text, true);  // isFinal = true (commit)
                        _lastPartialText = "";
                        _committed       = true;
                        _audio.ClearSession();
                    }
                    else
                    {
                        _committed       = false;
                        OnSegment?.Invoke(text, false);  // isFinal = false (partial live text)
                    }
                }

                // Clear stale audio after 3 s of silence
                if ((DateTime.UtcNow - _audio.LastVoiceActivity).TotalSeconds > 3.0)
                    _audio.ClearSession();
            }
        }
    }
}
