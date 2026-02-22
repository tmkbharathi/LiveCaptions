using System;

namespace LiveTranscriptionApp.Output
{
    /// <summary>
    /// Subtitle display output manager.
    ///
    /// Maintains a running `committedText` buffer across all committed utterances.
    /// On every update (partial or final), calls `SplitToLines` to distribute the
    /// full accumulated text across two subtitle lines — left-to-right, scrolling
    /// up when both lines are full.
    ///
    /// Designed for extension: inject an optional ITranslator to translate text
    /// before display.
    /// </summary>
    public class SubtitleOutputManager : IOutputManager
    {
        // ── Dependencies ───────────────────────────────────────────────────────
        private readonly Action<string> _setLine1;
        private readonly Action<string> _setLine2;

        /// <summary>Optional translation hook. Set before first use.</summary>
        public ITranslator? Translator { get; set; }

        // ── State ──────────────────────────────────────────────────────────────
        /// <summary>
        /// Approximate characters that fit on one subtitle line at 26px Segoe UI.
        /// Conservative value for screens ≥ 1366 px wide.
        /// </summary>
        public int CharsPerLine { get; set; } = 72;

        private string _committedText = "";

        // ── Constructor ────────────────────────────────────────────────────────
        /// <param name="setLine1">Callback to update line 1 in the UI (must be thread-safe / dispatched).</param>
        /// <param name="setLine2">Callback to update line 2 in the UI.</param>
        public SubtitleOutputManager(Action<string> setLine1, Action<string> setLine2)
        {
            _setLine1 = setLine1;
            _setLine2 = setLine2;
        }

        // ── IOutputManager ─────────────────────────────────────────────────────
        public void OnText(string text, bool isFinal)
        {
            // Filter noise / hallucinations
            if (string.IsNullOrWhiteSpace(text)
                || text.StartsWith("[")
                || text.StartsWith("(")
                || text.Contains("Thank you.")
                || text.Trim().Length < 2)
                return;

            text = text.Trim();

            // Optional translation
            if (Translator != null)
                text = Translator.Translate(text);

            if (isFinal)
            {
                // Append committed utterance to history
                _committedText = (_committedText + " " + text).Trim();

                // Cap to 4 line-widths to prevent unbounded growth
                int maxLen = CharsPerLine * 4;
                if (_committedText.Length > maxLen)
                {
                    int trimAt = _committedText.IndexOf(' ', _committedText.Length - maxLen);
                    if (trimAt > 0)
                        _committedText = _committedText.Substring(trimAt).TrimStart();
                }

                SplitToLines(_committedText);
            }
            else
            {
                // Live partial: committed history + current live words
                SplitToLines((_committedText + " " + text).Trim());
            }
        }

        // ── Line splitting ─────────────────────────────────────────────────────
        /// <summary>
        /// Distributes <paramref name="text"/> across two subtitle lines.
        /// Starts on line 1 and overflows left-to-right to line 2.
        /// When both lines are full, oldest text drops off and everything scrolls up.
        /// </summary>
        private void SplitToLines(string text)
        {
            text = text.Trim();
            int cpl = CharsPerLine;

            if (text.Length <= cpl)
            {
                // Fits entirely on line 1
                _setLine1(text);
                _setLine2("");
            }
            else if (text.Length <= cpl * 2)
            {
                // Spans both lines
                int split = text.LastIndexOf(' ', cpl);
                if (split < 0) split = cpl;
                _setLine1(text.Substring(0, split).TrimEnd());
                _setLine2(text.Substring(split).TrimStart());
            }
            else
            {
                // Overflows 2 lines → drop oldest, scroll up
                int dropUntil     = text.Length - cpl * 2;
                int wordBoundary  = text.IndexOf(' ', dropUntil);
                if (wordBoundary < 0) wordBoundary = dropUntil;
                string visible    = text.Substring(wordBoundary).TrimStart();

                int split = visible.LastIndexOf(' ', cpl);
                if (split < 0) split = Math.Min(cpl, visible.Length);
                _setLine1(visible.Substring(0, split).TrimEnd());
                _setLine2(visible.Substring(split).TrimStart());
            }
        }
    }

    // ── Translation extension point ────────────────────────────────────────────

    /// <summary>
    /// Optional translation interface. Implement and inject into SubtitleOutputManager
    /// to translate from English into any target language before display.
    /// </summary>
    public interface ITranslator
    {
        /// <param name="text">Source text (transcribed English).</param>
        /// <returns>Translated text.</returns>
        string Translate(string text);
    }
}
