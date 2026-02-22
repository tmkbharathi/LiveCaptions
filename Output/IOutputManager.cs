namespace LiveTranscriptionApp.Output
{
    /// <summary>
    /// Receives segmented text and decides what to do with it
    /// (display, translate, log, stream, etc.)
    /// </summary>
    public interface IOutputManager
    {
        /// <param name="text">The transcribed text.</param>
        /// <param name="isFinal">True = sentence committed; False = live partial.</param>
        void OnText(string text, bool isFinal);
    }
}
