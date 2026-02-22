using System;

namespace LiveTranscriptionApp.Segmentation
{
    /// <summary>
    /// Receives transcription text and emits segmented output.
    /// isFinal=true means a sentence boundary was detected (silence).
    /// isFinal=false means live, in-progress partial text.
    /// </summary>
    public interface ISentenceSegmenter
    {
        /// <summary>Fires whenever new text is ready. isFinal=true on sentence commit.</summary>
        event Action<string, bool> OnSegment;

        void Start();
        void Stop();
    }
}
