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
    public class VadSegmenter : ISentenceSegmenter, IDisposable
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
        private DateTime _continuousTagStartTime = DateTime.MinValue;

        // Minimum 0.2 s (2 chunks) of audio before first inference.
        private const int MinSessionChunks = 2;

        /// <param name="audio">AudioManager to consume chunks from.</param>
        /// <param name="whisper">Whisper engine for inference.</param>
        /// <param name="silenceMs">Milliseconds of silence before sentence commit (default 800).</param>
        /// <param name="inferenceIntervalMs">Minimum milliseconds between successive Whisper calls (default 300).</param>
        public VadSegmenter(AudioManager audio, WhisperEngine whisper, int silenceMs = 800, int inferenceIntervalMs = 300)
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
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException) { /* Already disposed by another thread, safe to ignore */ }

            _silenceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _silenceTimer?.Dispose();
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

                // Need at least 0.2 s (MinSessionChunks) before first inference
                if (_audio.SessionByteCount < MinSessionChunks * AudioManager.ChunkSize) continue;

                // Throttle: skip if we fired Whisper too recently
                var now = DateTime.UtcNow;
                if ((now - _lastInferenceTime).TotalMilliseconds < _inferenceIntervalMs) continue;

                // Transcribe fixed-size rolling session snapshot
                var snapshot = _audio.GetSessionSnapshot();
                string text  = await _whisper.TranscribeAsync(snapshot);
                _lastInferenceTime = DateTime.UtcNow;
                // Check if the output consists entirely of non-speech tags
                string stripped = System.Text.RegularExpressions.Regex.Replace(text, @"\[.*?\]", "");
                stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"\(.*?\)", "");
                stripped = stripped.Replace("♪", "").Trim();

                bool isPureTag = (stripped.Length < 2 && text.Trim().Length >= 2);

                if (!isPureTag)
                {
                    // Strip out hallucinatory non-speech tags (e.g. [grunt]) inside normal sentences
                    text = stripped;
                    _continuousTagStartTime = DateTime.MinValue;
                    
                    if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
                        continue;
                }
                else
                {
                    // If it is purely a tag like [music], wait 4 seconds of continuous tags before displaying it.
                    if (_continuousTagStartTime == DateTime.MinValue)
                        _continuousTagStartTime = DateTime.UtcNow;

                    if ((DateTime.UtcNow - _continuousTagStartTime).TotalSeconds < 4.0)
                        continue; // Hide it until 4 seconds have passed

                    text = text.Trim(); // Allow the pure tag to pass through
                }
                
                // Filter common Whisper silence hallucinations
                if (text.Equals("Thank you.", StringComparison.OrdinalIgnoreCase) || 
                    text.Equals("Thank you", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Hallucination Drop Protection
                // If the new text completely loses the context of the previous long text (common with sudden loud noises),
                // we forcefully commit the previous text so it doesn't get erased from the screen.
                if (!string.IsNullOrEmpty(_lastPartialText))
                {
                    var oldW = _lastPartialText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var newW = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    if (oldW.Length >= 3 && newW.Length > 0 && newW.Length < oldW.Length)
                    {
                        int matchCount = 0;
                        foreach (var w1 in oldW)
                        {
                            if (w1.Length <= 2) continue; // Ignore short filler words
                            foreach (var w2 in newW)
                            {
                                if (string.Equals(w1, w2, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchCount++;
                                    break;
                                }
                            }
                        }

                        int oldSigWords = System.Linq.Enumerable.Count(oldW, x => x.Length > 2);
                        if (oldSigWords >= 2 && matchCount == 0)
                        {
                            // Context completely broken! Force commit old string to protect it.
                            OnSegment?.Invoke(_lastPartialText, true);
                            _audio.ClearSession();
                            _lastPartialText = text;
                            _committed = false;
                            OnSegment?.Invoke(text, false);
                            continue;
                        }
                    }
                }

                _lastPartialText = text;
                    // Sliding Window: Max Length Safety Net (Wait for 800ms Silence Timer for natural breaks)
                    bool reachedMaxLength = _audio.SessionByteCount >= (100 * AudioManager.ChunkSize); // 10 seconds

                    if (reachedMaxLength)
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

                // Clear stale audio after 3 s of silence
                if ((DateTime.UtcNow - _audio.LastVoiceActivity).TotalSeconds > 3.0)
                    _audio.ClearSession();
            }
        }
    }
}
