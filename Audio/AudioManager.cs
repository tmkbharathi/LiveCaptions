using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace LiveTranscriptionApp.Audio
{
    /// <summary>
    /// Sits between an IAudioResource and the transcription pipeline.
    /// - Accumulates raw PCM bytes into fixed-size chunks (0.25 s each)
    /// - Maintains a FIXED-SIZE rolling session window (max 8 chunks = 2 s)
    ///   so every Whisper inference always operates on the same constant window,
    ///   eliminating the "sliding window grows to 3 s and stays maxed" slowdown.
    /// - Tracks the timestamp of the last voice activity
    /// </summary>
    public class AudioManager
    {
        // ── Constants ─────────────────────────────────────────────────────────
        // 16 kHz × 2 bytes per sample = 32 000 bytes per second
        public const int SampleRate    = 16000;
        public const int BytesPerFrame = 2;               // S16LE
        public const int ChunkSize     = SampleRate * BytesPerFrame / 4;  // 0.25 s = 8000 bytes

        /// <summary>
        /// Maximum number of chunks kept in the rolling session window.
        /// 120 chunks × 0.25 s = 30 s — Safety net window to prevent memory explosion, 
        /// but long enough to capture natural sentence structures (sliding window).
        /// </summary>
        public const int MaxSessionChunks = 120;          // 30 s

        private const float SilenceThreshold = 0.05f;

        // ── Internal state ─────────────────────────────────────────────────────
        private readonly List<byte>             _audioBuffer   = new();
        private readonly Queue<byte[]>          _sessionQueue  = new(); // rolling window
        private readonly object                 _bufferLock    = new();
        private readonly ConcurrentQueue<byte[]> _chunkQueue   = new();
        private readonly SemaphoreSlimWrapper   _signal        = new();

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

        /// <summary>
        /// Dequeue one incoming chunk and push it onto the rolling session window.
        /// Evicts the oldest chunk when the window is full (keeps window = MaxSessionChunks).
        /// </summary>
        public bool TryConsumeChunk()
        {
            if (!_chunkQueue.TryDequeue(out var chunk)) return false;
            lock (_bufferLock)
            {
                _sessionQueue.Enqueue(chunk);
                // Evict oldest chunk to keep window constant-size
                while (_sessionQueue.Count > MaxSessionChunks)
                    _sessionQueue.Dequeue();
            }
            return true;
        }

        /// <summary>
        /// Drain all queued incoming chunks into the rolling session window.
        /// Used while inference is busy so audio is not dropped from the queue.
        /// </summary>
        public void DrainQueue()
        {
            lock (_bufferLock)
            {
                while (_chunkQueue.TryDequeue(out var chunk))
                {
                    _sessionQueue.Enqueue(chunk);
                    while (_sessionQueue.Count > MaxSessionChunks)
                        _sessionQueue.Dequeue();
                }
            }
        }

        /// <summary>Flatten the rolling session window into a contiguous byte array for Whisper.</summary>
        public byte[] GetSessionSnapshot()
        {
            lock (_bufferLock)
            {
                // Concatenate all chunks in order — each chunk is fixed-size so this is O(n) chunks
                var result = new byte[_sessionQueue.Count * ChunkSize];
                int offset = 0;
                foreach (var chunk in _sessionQueue)
                {
                    Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                    offset += chunk.Length;
                }
                return result;
            }
        }

        public int SessionByteCount
        {
            get { lock (_bufferLock) { return _sessionQueue.Count * ChunkSize; } }
        }

        /// <summary>Clear the rolling session window (called after sentence commit or long silence).</summary>
        public void ClearSession()
        {
            lock (_bufferLock) { _sessionQueue.Clear(); }
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
