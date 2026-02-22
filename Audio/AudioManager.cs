using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LiveTranscriptionApp.Audio
{
    /// <summary>
    /// Sits between an IAudioResource and the transcription pipeline.
    /// - Accumulates raw PCM bytes into fixed-size chunks (0.25 s)
    /// - Provides a session buffer (the growing window passed to Whisper)
    /// - Tracks the timestamp of the last voice activity
    /// </summary>
    public class AudioManager
    {
        // ── Constants ─────────────────────────────────────────────────────────
        // 16 kHz × 2 bytes per sample = 32 000 bytes per second
        public const int SampleRate    = 16000;
        public const int BytesPerFrame = 2;               // S16LE
        public const int ChunkSize     = SampleRate * BytesPerFrame / 4;  // 0.25 s
        public const int MaxSessionBytes = SampleRate * BytesPerFrame * 3; // 3 s cap

        private const float SilenceThreshold = 0.05f;

        // ── Internal state ─────────────────────────────────────────────────────
        private readonly List<byte>           _audioBuffer   = new();
        private readonly List<byte>           _sessionBuffer = new();
        private readonly object               _bufferLock    = new();
        private readonly ConcurrentQueue<byte[]> _chunkQueue  = new();
        private readonly SemaphoreSlimWrapper _signal        = new();

        public DateTime LastVoiceActivity { get; private set; } = DateTime.UtcNow;
        public event Action<float>? OnLevelChanged;

        // ── IAudioResource subscription ────────────────────────────────────────
        public void Attach(IAudioResource source)
        {
            source.OnAudioData += HandleAudioData;
            source.OnAudioData += _ => CheckLevel(source.Level);
        }

        private void CheckLevel(float level)
        {
            if (level > SilenceThreshold)
                LastVoiceActivity = DateTime.UtcNow;
            OnLevelChanged?.Invoke(level);
        }

        private void HandleAudioData(byte[] pcmData)
        {
            lock (_bufferLock)
            {
                _audioBuffer.AddRange(pcmData);
                while (_audioBuffer.Count >= ChunkSize)
                {
                    var chunk = _audioBuffer.GetRange(0, ChunkSize).ToArray();
                    _audioBuffer.RemoveRange(0, ChunkSize);
                    _chunkQueue.Enqueue(chunk);
                    _signal.Release();
                }
            }
        }

        // ── For the transcription loop ─────────────────────────────────────────

        /// <summary>Wait until at least one chunk is available.</summary>
        public System.Threading.Tasks.Task WaitAsync() => _signal.WaitAsync();

        /// <summary>Attempt to dequeue a chunk and append it to the session buffer.</summary>
        public bool TryConsumeChunk()
        {
            if (!_chunkQueue.TryDequeue(out var chunk)) return false;
            lock (_bufferLock)
            {
                _sessionBuffer.AddRange(chunk);
                if (_sessionBuffer.Count > MaxSessionBytes)
                    _sessionBuffer.RemoveRange(0, _sessionBuffer.Count - MaxSessionBytes);
            }
            return true;
        }

        /// <summary>Drain all queued chunks into session (used while inference is busy).</summary>
        public void DrainQueue()
        {
            while (_chunkQueue.TryDequeue(out var chunk))
            {
                lock (_bufferLock)
                {
                    _sessionBuffer.AddRange(chunk);
                    if (_sessionBuffer.Count > MaxSessionBytes)
                        _sessionBuffer.RemoveRange(0, _sessionBuffer.Count - MaxSessionBytes);
                }
            }
        }

        /// <summary>Snapshot of the current session buffer for Whisper inference.</summary>
        public byte[] GetSessionSnapshot()
        {
            lock (_bufferLock) { return _sessionBuffer.ToArray(); }
        }

        public int SessionByteCount
        {
            get { lock (_bufferLock) { return _sessionBuffer.Count; } }
        }

        /// <summary>Clear session buffer (called after sentence commit or long silence).</summary>
        public void ClearSession()
        {
            lock (_bufferLock) { _sessionBuffer.Clear(); }
        }

        // ── Thin SemaphoreSlim wrapper so callers don't depend on Threading directly
        private class SemaphoreSlimWrapper
        {
            private readonly System.Threading.SemaphoreSlim _sem = new(0);
            public void Release() => _sem.Release();
            public System.Threading.Tasks.Task WaitAsync() => _sem.WaitAsync();
        }
    }
}
