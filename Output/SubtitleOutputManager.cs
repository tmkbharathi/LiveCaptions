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
        private readonly Action<string>[] _setLines;

        /// <summary>Optional translation hook. Set before first use.</summary>
        public ITranslator? Translator { get; set; }

        // ── State ──────────────────────────────────────────────────────────────
        /// <summary>
        /// Approximate characters that fit on one subtitle line at 26px Segoe UI.
        /// Conservative value for screens ≥ 1366 px wide.
        /// </summary>
        public int CharsPerLine { get; set; } = 72;
        
        /// <summary>
        /// How many lines are currently visible on screen. Determines when to scroll text upwards.
        /// </summary>
        public int VisibleLines { get; set; } = 2;

        // "Block-level snapping" state
        private string _committedHistory = "";
        private string _frozenLine1      = "";

        /// <param name="setLines">Callback array to update each line block in the UI (up to 10 lines supported).</param>
        public SubtitleOutputManager(params Action<string>[] setLines)
        {
            _setLines = setLines;
            if (_setLines.Length == 0 || _setLines.Length > 10)
                throw new ArgumentException("SubtitleOutputManager requires between 1 and 10 line dispatchers.");
        }

        // ── IOutputManager ─────────────────────────────────────────────────────
        public void OnText(string text, bool isFinal)
        {
            if (!Preferences.ShowAudioTags)
            {
                // Strip bracketed audio events like [music], (explosion), etc.
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\[.*?\]", "");
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\(.*?\)", "");
                text = text.Replace("♪", "");
            }
            
            text = text.Trim();

            // Filter noise / hallucinations
            if (string.IsNullOrWhiteSpace(text)
                || text.Contains("Thank you.")
                || text.Length < 2)
                return;

            text = text.Trim();

            // Run Profanity Filter
            if (Preferences.FilterProfanity)
            {
                var badWords = new[] { "fuck", "shit", "bitch", "asshole", "damn", "cunt", "fucking", "bullshit" };
                foreach (var word in badWords)
                {
                    // Case-insensitive replace with asterisks
                    text = System.Text.RegularExpressions.Regex.Replace(
                        text, 
                        $@"\b{word}\b", 
                        "***", 
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
            }

            // Optional translation
            if (Translator != null)
                text = Translator.Translate(text);

            if (isFinal)
            {
                // Append the fully committed sentence to history safely
                _committedHistory = MergeWithOverlap(_committedHistory, text);

                // To prevent infinite memory growth over hours, cap history
                // We parse it into lines to ensure we only trim EXACTLY at line boundaries,
                // so we don't accidentally shift the wrapping of the remaining words!
                var lines = GetLines(_committedHistory);
                int keepLines = Math.Max(2, VisibleLines);
                if (lines.Count > keepLines)
                {
                    // Keep the last `VisibleLines` full lines to preserve exact word alignment on screen
                    _committedHistory = string.Join(" ", System.Linq.Enumerable.Skip(lines, lines.Count - keepLines));
                }

                ProcessDisplayBlocks(_committedHistory);

            }
            else
            {
                // Live preview: history + live incoming segment
                ProcessDisplayBlocks(MergeWithOverlap(_committedHistory, text));
            }
        }

        /// <summary>
        /// Detects if the start of the new text overlaps with the end of the history.
        /// Fixes duplicate words caused by audio chunk boundaries, even when Whisper revises words mid-sentence.
        /// </summary>
        private string MergeWithOverlap(string history, string addition)
        {
            if (string.IsNullOrWhiteSpace(history)) return addition;
            if (string.IsNullOrWhiteSpace(addition)) return history;

            var hWords = history.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var aWords = addition.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Find the most recent, reliable overlap anchor between the history and the new addition.
            // We search for an anchor of up to 5 words, from end to beginning in history.
            int maxAnchor = Math.Min(aWords.Length, Math.Min(hWords.Length, 5));
            int minAnchor = 2; // Require at least 2 words to anchor

            for (int anchorLen = maxAnchor; anchorLen >= minAnchor; anchorLen--)
            {
                // We typically only need to check the last 100 words of history for the overlap
                int searchStart = Math.Max(0, hWords.Length - 100);
                for (int i = hWords.Length - anchorLen; i >= searchStart; i--)
                {
                    bool match = true;
                    for (int j = 0; j < anchorLen; j++)
                    {
                        // Clean punctuation for comparison
                        string hWord = hWords[i + j].TrimEnd('.', ',', '?', '!', '\"', '\'').TrimStart('\"', '\'');
                        string aWord = aWords[j].TrimEnd('.', ',', '?', '!', '\"', '\'').TrimStart('\"', '\'');
                        if (!string.Equals(hWord, aWord, StringComparison.OrdinalIgnoreCase))
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        // Anchor found! 'addition' starts at index 'i' in history.
                        // We keep everything in history BEFORE 'i', and then append 'addition'.
                        string prefix = string.Join(" ", System.Linq.Enumerable.Take(hWords, i));
                        return string.IsNullOrWhiteSpace(prefix) ? addition : prefix + " " + addition;
                    }
                }
            }

            // Fallback: strict suffix-prefix match for very short overlaps (1 word)
            int bestOverlap = 0;
            int maxExactOverlap = Math.Min(hWords.Length, aWords.Length);
            for (int overlap = 1; overlap <= maxExactOverlap; overlap++)
            {
                bool match = true;
                for (int i = 0; i < overlap; i++)
                {
                    string hWord = hWords[hWords.Length - overlap + i].TrimEnd('.', ',', '?', '!', '\"', '\'').TrimStart('\"', '\'');
                    string aWord = aWords[i].TrimEnd('.', ',', '?', '!', '\"', '\'').TrimStart('\"', '\'');
                    if (!string.Equals(hWord, aWord, StringComparison.OrdinalIgnoreCase))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) bestOverlap = overlap;
            }

            if (bestOverlap > 0)
            {
                string newAddition = string.Join(" ", System.Linq.Enumerable.Skip(aWords, bestOverlap));
                return (history + " " + newAddition).Trim();
            }

            // No overlap found, just concatenate
            return (history + " " + addition).Trim();
        }

        // ── Block Dispatcher ────────────────────────────────────────

        /// <summary>
        /// Instantly updates the display with the latest full session text.
        /// </summary>
        private void ProcessDisplayBlocks(string fullSessionText)
        {
            /// Instantly apply the layout and push to UI, discarding the old typewriter animation.
            /// Whisper's rapid partial updates and corrections are better served by instant display
            /// than fake typing which causes obvious backspacing animations when resolving context.
            RenderLayout(fullSessionText);
        }



        private System.Collections.Generic.List<string> GetLines(string textToRender)
        {
            var lines = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(textToRender)) return lines;

            string currentLine = "";
            string[] words = textToRender.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                bool fits = currentLine.Length == 0 ? word.Length <= CharsPerLine : (currentLine.Length + 1 + word.Length) <= CharsPerLine;
                if (fits)
                {
                    currentLine = currentLine.Length == 0 ? word : currentLine + " " + word;
                }
                else
                {
                    lines.Add(currentLine);
                    currentLine = word;
                }
            }
            if (currentLine.Length > 0) lines.Add(currentLine);
            
            return lines;
        }

        /// <summary>
        /// Calculates the layout of text into two discrete subtitle blocks.
        /// Text starts at the bottom line and smoothly pushes upwards (rollup).
        /// </summary>
        private void RenderLayout(string textToRender)
        {
            if (string.IsNullOrWhiteSpace(textToRender))
            {
                string[] emptyLines = new string[_setLines.Length];
                for(int i=0; i<emptyLines.Length; i++) emptyLines[i] = " ";
                Render(emptyLines);
                return;
            }

            // --- DEBUG: Save to desktop ---
            try 
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string filePath = System.IO.Path.Combine(desktopPath, "debug_subtitles.txt");
                System.IO.File.AppendAllText(filePath, textToRender + Environment.NewLine);
            }
            catch { /* Ignore debug write errors */ }
            // ------------------------------


            var lines = GetLines(textToRender);
            int maxLines = Math.Min(VisibleLines, _setLines.Length);

            string[] displayLines = new string[_setLines.Length];
            for (int i = 0; i < _setLines.Length; i++) displayLines[i] = "";

            if (lines.Count <= maxLines)
            {
                // If the text fits in the available layout boxes, just fill them top to bottom
                for (int i = 0; i < lines.Count; i++)
                {
                    displayLines[i] = lines[i];
                }
            }
            else
            {
                // Push older text up, newer text stays on bottom (scrolling effect).
                int startIdx = lines.Count - maxLines;
                for (int i = 0; i < maxLines; i++)
                {
                    displayLines[i] = lines[startIdx + i];
                }

                // "Pinning" logic: Prevent the top line from fluttering if Whisper just corrected 
                // a word on the bottom line. If the new top line starts with the *exact same text* 
                // as the previous top line, keep the previous top line intact so it doesn't cause a re-wrap shift.
                if (!string.IsNullOrEmpty(_frozenLine1) && displayLines[0].StartsWith(_frozenLine1, StringComparison.OrdinalIgnoreCase))
                {
                    displayLines[0] = _frozenLine1; 
                }
            }

            _frozenLine1 = displayLines[0];
            Render(displayLines);
        }

        private void Render(string[] dLines)
        {
            for (int i = 0; i < _setLines.Length; i++)
            {
                _setLines[i](string.IsNullOrEmpty(dLines[i]) ? " " : dLines[i]);
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
