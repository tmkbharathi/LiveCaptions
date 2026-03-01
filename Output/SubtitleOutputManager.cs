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
        // ── Pre-Compiled Regex ─────────────────────────────────────────────────
        private static readonly System.Text.RegularExpressions.Regex TagRegexBracket = new(@"\[.*?\]", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex TagRegexParen = new(@"\(.*?\)", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex ProfanityRegex = new(
            @"\b(fuck|shit|bitch|asshole|damn|cunt|fucking|bullshit)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
                text = TagRegexBracket.Replace(text, "");
                text = TagRegexParen.Replace(text, "");
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
                text = ProfanityRegex.Replace(text, "***");
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

            int bestOverlap = 0;

            // Find overlapping words without allocating arrays
            for (int i = 1; i <= Math.Min(history.Length, addition.Length); i++)
            {
                // To safely check word boundaries we only check overlaps where a word boundary exists
                // Addition prefix length i
                if (i < addition.Length && !char.IsWhiteSpace(addition[i]) && i != addition.Length) continue;

                // history suffix length i
                if (history.Length - i - 1 >= 0 && !char.IsWhiteSpace(history[history.Length - i - 1])) continue;

                var hSub = history.AsSpan(history.Length - i);
                var aSub = addition.AsSpan(0, i);

                // Ignore punctuation mismatches ( Whisper will often change punctuation rapidly between inferences )
                int hTrimStart = 0, hTrimEnd = hSub.Length;
                while (hTrimStart < hSub.Length && char.IsPunctuation(hSub[hTrimStart])) hTrimStart++;
                while (hTrimEnd > hTrimStart && char.IsPunctuation(hSub[hTrimEnd - 1])) hTrimEnd--;

                int aTrimStart = 0, aTrimEnd = aSub.Length;
                while (aTrimStart < aSub.Length && char.IsPunctuation(aSub[aTrimStart])) aTrimStart++;
                while (aTrimEnd > aTrimStart && char.IsPunctuation(aSub[aTrimEnd - 1])) aTrimEnd--;

                if (hSub.Slice(hTrimStart, hTrimEnd - hTrimStart).Equals(aSub.Slice(aTrimStart, aTrimEnd - aTrimStart), StringComparison.OrdinalIgnoreCase))
                {
                    bestOverlap = i;
                }
            }

            if (bestOverlap > 0)
            {
                string newAddition = addition.Substring(bestOverlap).TrimStart();
                return (history + (newAddition.Length > 0 ? " " : "") + newAddition).Trim();
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

            string[] words = textToRender.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return lines;

            var sb = new System.Text.StringBuilder(CharsPerLine);
            sb.Append(words[0]);

            for (int i = 1; i < words.Length; i++)
            {
                if (sb.Length + 1 + words[i].Length <= CharsPerLine)
                {
                    sb.Append(' ').Append(words[i]);
                }
                else
                {
                    lines.Add(sb.ToString());
                    sb.Clear();
                    sb.Append(words[i]);
                }
            }
            if (sb.Length > 0)
            {
                lines.Add(sb.ToString());
            }
            
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
