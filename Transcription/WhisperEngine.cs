using System;
using System.IO;
using System.Threading.Tasks;
using Whisper.net;

namespace LiveTranscriptionApp.Transcription
{
    /// <summary>
    /// Whisper.net implementation of ITranscriptionEngine.
    /// Wraps WhisperFactory + WhisperProcessor and adds WAV header generation.
    /// Thread-safe via _isBusy guard — returns "" if called while busy.
    /// </summary>
    public class WhisperEngine : ITranscriptionEngine, IAsyncDisposable
    {
        private WhisperFactory?    _factory;
        private WhisperProcessor?  _processor;
        private volatile bool      _isBusy;

        public Task InitializeAsync(string modelPath)
        {
            _factory   = WhisperFactory.FromPath(modelPath);
            _processor = _factory.CreateBuilder()
                .WithLanguage("auto")
                .WithThreads(Environment.ProcessorCount) // Maximize CPU core usage
                .Build();
            return Task.CompletedTask;
        }

        public async Task<string> TranscribeAsync(System.Collections.Generic.IReadOnlyList<byte[]> pcmChunks)
        {
            if (_processor == null)
                throw new InvalidOperationException("Call InitializeAsync first.");

            // Skip if already processing (caller should check _isBusy before calling)
            if (_isBusy) return string.Empty;

            _isBusy = true;
            try
            {
                using var wavStream = new ChunkedWavStream(pcmChunks);
                string result = "";
                await foreach (var seg in _processor.ProcessAsync(wavStream))
                {
                    // Strictly enforce English-only. If someone speaks Spanish, 
                    // this prevents the engine from hallucinating English translations.
                    if (seg.Language == "en")
                    {
                        result += seg.Text;
                    }
                }
                return result.Trim();
            }
            finally
            {
                _isBusy = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_processor != null)
            {
                await _processor.DisposeAsync();
                _processor = null;
            }
            _factory?.Dispose();
            _factory = null;
        }

        public bool IsBusy => _isBusy;

        // ── Zero-Allocation Chunked WAV Stream ─────────────────────────────────
        private class ChunkedWavStream : Stream
        {
            private readonly System.Collections.Generic.IReadOnlyList<byte[]> _chunks;
            private readonly int _chunkSize;
            private readonly long _length;
            private long _position;
            private readonly byte[] _wavHeader;

            public ChunkedWavStream(System.Collections.Generic.IReadOnlyList<byte[]> chunks, int chunkSize = Audio.AudioManager.ChunkSize)
            {
                _chunks = chunks;
                _chunkSize = chunkSize;
                int dataLength = chunks.Count * chunkSize;
                _wavHeader = BuildWavHeader(dataLength);
                _length = _wavHeader.Length + dataLength;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _length) return 0;
                int originalCount = count;

                // Read from header
                if (_position < _wavHeader.Length)
                {
                    int headerBytes = (int)Math.Min(count, _wavHeader.Length - _position);
                    Buffer.BlockCopy(_wavHeader, (int)_position, buffer, offset, headerBytes);
                    _position += headerBytes;
                    offset += headerBytes;
                    count -= headerBytes;
                }

                // Read from chunks
                while (count > 0 && _position < _length)
                {
                    long pcmPosition = _position - _wavHeader.Length;
                    
                    int chunkIndex = (int)(pcmPosition / _chunkSize);
                    int offsetInChunk = (int)(pcmPosition % _chunkSize);

                    // If we somehow go past the chunks but length says we aren't done (shouldn't happen)
                    if (chunkIndex >= _chunks.Count)
                        break; 

                    var chunk = _chunks[chunkIndex];
                    
                    // The actual valid bytes remaining in the current chunk
                    int bytesAvailableInChunk = chunk.Length - offsetInChunk;
                    
                    // We can only read up to the end of the current chunk, or the requested count
                    int bytesToRead = Math.Min(count, bytesAvailableInChunk);
                    
                    // Ensure we don't read past the total declared length
                    long remainingTotalLength = _length - _position;
                    if (bytesToRead > remainingTotalLength)
                        bytesToRead = (int)remainingTotalLength;

                    if (bytesToRead <= 0) break; // safeguard

                    Buffer.BlockCopy(chunk, offsetInChunk, buffer, offset, bytesToRead);

                    _position += bytesToRead;
                    offset += bytesToRead;
                    count -= bytesToRead;
                }

                return originalCount - count;
            }

            public override void Flush() {}
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            private static byte[] BuildWavHeader(int dataLength)
            {
                using var ms = new MemoryStream(44);
                using var writer = new System.IO.BinaryWriter(ms);

                writer.Write("RIFF".ToCharArray());
                writer.Write(36 + dataLength);
                writer.Write("WAVE".ToCharArray());
                writer.Write("fmt ".ToCharArray());
                writer.Write(16);              // subchunk size
                writer.Write((short)1);        // PCM
                writer.Write((short)1);        // Mono
                writer.Write(16000);           // Sample rate
                writer.Write(16000 * 2);       // Byte rate
                writer.Write((short)2);        // Block align
                writer.Write((short)16);       // Bits per sample
                writer.Write("data".ToCharArray());
                writer.Write(dataLength);

                return ms.ToArray();
            }
        }
    }
}
