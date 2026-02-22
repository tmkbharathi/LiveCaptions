namespace LiveTranscriptionApp.Audio
{
    /// <summary>
    /// Delegate for raw PCM audio data arriving from any source.
    /// </summary>
    public delegate void AudioDataHandler(byte[] pcmData);

    /// <summary>
    /// Abstraction over any audio input source (GStreamer loopback, mic, file, etc.)
    /// Produces 16kHz, Mono, 16-bit signed PCM (S16LE).
    /// </summary>
    public interface IAudioResource
    {
        /// <summary>Fires whenever a new chunk of PCM data is ready.</summary>
        event AudioDataHandler OnAudioData;

        /// <summary>Current normalised audio level [0.0 – 1.0].</summary>
        float Level { get; }

        /// <summary>Start capturing audio.</summary>
        void Start();

        /// <summary>Stop capturing audio.</summary>
        void Stop();
    }
}
