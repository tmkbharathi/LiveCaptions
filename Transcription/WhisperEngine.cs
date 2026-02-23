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
    public class WhisperEngine : ITranscriptionEngine
    {
        private WhisperFactory?    _factory;
        private WhisperProcessor?  _processor;
        private volatile bool      _isBusy;

        public Task InitializeAsync(string modelPath)
        {
            _factory   = WhisperFactory.FromPath(modelPath);
            _processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(Environment.ProcessorCount) // Maximize CPU core usage
                .Build();
            return Task.CompletedTask;
        }

        public async Task<string> TranscribeAsync(byte[] pcmBytes)
        {
            if (_processor == null)
                throw new InvalidOperationException("Call InitializeAsync first.");

            // Skip if already processing (caller should check _isBusy before calling)
            if (_isBusy) return string.Empty;

            _isBusy = true;
            try
            {
                var wavStream = new MemoryStream(BuildWavHeader(pcmBytes));
                string result = "";
                await foreach (var seg in _processor.ProcessAsync(wavStream))
                {
                    result += seg.Text;
                }
                return result.Trim();
            }
            finally
            {
                _isBusy = false;
            }
        }

        public bool IsBusy => _isBusy;

        // ── WAV header construction ────────────────────────────────────────────
        private static byte[] BuildWavHeader(byte[] samples)
        {
            using var ms     = new MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);

            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + samples.Length);
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
            writer.Write(samples.Length);
            writer.Write(samples);

            return ms.ToArray();
        }
    }
}
