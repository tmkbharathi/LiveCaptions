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

        // "Block-level snapping" state
        private string _committedHistory = "";
        private string _frozenLine1      = "";

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
                // Append the fully committed sentence to history
                _committedHistory = (_committedHistory + " " + text).Trim();

                // To prevent infinite memory growth over hours, cap history
                int maxLen = CharsPerLine * 4;
                if (_committedHistory.Length > maxLen)
                {
                    int trimAt = _committedHistory.IndexOf(' ', _committedHistory.Length - maxLen);
                    if (trimAt > 0)
                        _committedHistory = _committedHistory.Substring(trimAt).TrimStart();
                }

                ProcessDisplayBlocks(_committedHistory);
            }
            else
            {
                // Live preview: history + live incoming segment
                ProcessDisplayBlocks((_committedHistory + " " + text).Trim());
            }
        }

        // ── Block Dispatecer ───────────────────────────────────────────────────
        /// <summary>
        /// Calculates the layout of text into two discrete subtitle blocks.
        /// Line 1 freezes in place. When line 2 overflows, line 2 instantly snaps
        /// up to line 1 and clears line 2.
        /// </summary>
        private void ProcessDisplayBlocks(string fullSessionText)
        {
            if (string.IsNullOrEmpty(fullSessionText))
            {
                Render("", "");
                return;
            }

            // We must rebuild the visual state exactly as if the text was typed left-to-right.
            string currentLine1 = "";
            string currentLine2 = "";

            // Split the full continuous incoming text into words
            string[] words = fullSessionText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                // Can the word fit on the active line we are currently filling?
                // Logic: If Line 1 is not full, fill Line 1.
                //        If Line 1 is full, fill Line 2.
                //        If Line 2 is full, snap Line 2 into Line 1, and make the current word the start of a new Line 2.

                bool fillsLine1 = currentLine1.Length == 0 ? word.Length <= CharsPerLine : (currentLine1.Length + 1 + word.Length) <= CharsPerLine;
                
                if (fillsLine1 && currentLine2.Length == 0) 
                {
                    // Filling Line 1 left-to-right
                    currentLine1 = currentLine1.Length == 0 ? word : currentLine1 + " " + word;
                }
                else
                {
                    // Line 1 is full (or frozen). Now filling Line 2 left-to-right.
                    bool fillsLine2 = currentLine2.Length == 0 ? word.Length <= CharsPerLine : (currentLine2.Length + 1 + word.Length) <= CharsPerLine;

                    if (fillsLine2)
                    {
                        currentLine2 = currentLine2.Length == 0 ? word : currentLine2 + " " + word;
                    }
                    else
                    {
                        // **SNAP TRIGGERED**: Line 2 is completely full. 
                        // The entire Line 2 instantly snaps upwards to become the new Line 1.
                        currentLine1 = currentLine2;
                        // The word that caused the overflow becomes the start of the brand new empty Line 2.
                        currentLine2 = word;
                    }
                }
            }

            _frozenLine1 = currentLine1;
            Render(currentLine1, currentLine2);
        }

        private void Render(string l1, string l2)
        {
            _setLine1(string.IsNullOrEmpty(l1) ? " " : l1);
            _setLine2(string.IsNullOrEmpty(l2) ? " " : l2);
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
