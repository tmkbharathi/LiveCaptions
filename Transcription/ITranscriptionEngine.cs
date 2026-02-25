using System.Threading.Tasks;

namespace LiveTranscriptionApp.Transcription
{
    /// <summary>
    /// Converts raw PCM audio bytes to a transcribed string.
    /// </summary>
    public interface ITranscriptionEngine
    {
        /// <summary>Load the model from disk. Must be called before TranscribeAsync.</summary>
        Task InitializeAsync(string modelPath);

        /// <summary>
        /// Transcribe a sequence of raw 16kHz Mono S16LE PCM chunks.
        /// Returns the recognized text, or empty string if nothing was detected.
        /// </summary>
        Task<string> TranscribeAsync(System.Collections.Generic.IReadOnlyList<byte[]> pcmChunks);
    }
}
