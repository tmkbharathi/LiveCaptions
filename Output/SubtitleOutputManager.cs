using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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
        private static readonly Regex TagRegexBracket = new(@"\[.*?\]", RegexOptions.Compiled);
        private static readonly Regex TagRegexParen = new(@"\(.*?\)", RegexOptions.Compiled);
        private static readonly Regex ProfanityRegex = new(
            @"\b(fuck|shit|bitch|asshole|damn|cunt|fucking|bullshit)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                // Fast-path checks to avoid regex engine overhead when no tags are present
                if (text.Contains('[')) text = TagRegexBracket.Replace(text, "");
                if (text.Contains('(')) text = TagRegexParen.Replace(text, "");
                if (text.Contains('♪')) text = text.Replace("♪", "");
            }
            
            text = text.Trim();

            // Filter noise / hallucinations
            if (string.IsNullOrWhiteSpace(text)
                || text.Contains("Thank you.")
                || text.Length < 2)
                return;

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
                    _committedHistory = string.Join(" ", lines.Skip(lines.Count - keepLines));
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

            string[] hWords = history.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string[] aWords = addition.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            int maxOverlapWords = Math.Min(hWords.Length, Math.Min(aWords.Length, 15)); // Limit to 15 words
            int bestOverlapCount = 0;

            for (int overlapCount = 1; overlapCount <= maxOverlapWords; overlapCount++)
            {
                int matchCount = 0;
                for (int i = 0; i < overlapCount; i++)
                {
                    string hWord = StripPunctuation(hWords[hWords.Length - overlapCount + i]);
                    string aWord = StripPunctuation(aWords[i]);

                    if (string.Equals(hWord, aWord, StringComparison.OrdinalIgnoreCase))
                    {
                        matchCount++;
                    }
                    else if (hWord.Length > 3 && aWord.Length > 3 && ComputeLevenshtein(hWord.ToLowerInvariant(), aWord.ToLowerInvariant()) <= 1)
                    {
                        matchCount++;
                    }
                }

                if (overlapCount <= 2 && matchCount == overlapCount)
                    bestOverlapCount = overlapCount;
                else if (overlapCount > 2 && overlapCount <= 5 && matchCount >= overlapCount - 1)
                    bestOverlapCount = overlapCount;
                else if (overlapCount > 5 && matchCount >= overlapCount - 2)
                    bestOverlapCount = overlapCount;
            }

            if (bestOverlapCount > 0)
            {
                var remainingAddition = string.Join(" ", aWords.Skip(bestOverlapCount));
                return remainingAddition.Length > 0 ? $"{history} {remainingAddition}" : history;
            }

            return $"{history} {addition}";
        }

        private static string StripPunctuation(string word)
        {
            int start = 0, end = word.Length;
            while (start < end && char.IsPunctuation(word[start])) start++;
            while (end > start && char.IsPunctuation(word[end - 1])) end--;
            return start < end ? word.Substring(start, end - start) : "";
        }

        private static int ComputeLevenshtein(string s, string t)
        {
            int n = s.Length, m = t.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            int[] d = new int[m + 1];
            for (int i = 0; i <= m; i++) d[i] = i;
            for (int i = 1; i <= n; i++)
            {
                int prev = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    int temp = d[j];
                    d[j] = Math.Min(Math.Min(d[j] + 1, prev + 1), d[j - 1] + cost);
                    d[j - 1] = prev;
                    prev = temp;
                }
                d[m] = prev;
            }
            return d[m];
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



        private List<string> GetLines(string textToRender)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(textToRender)) return lines;

            ReadOnlySpan<char> span = textToRender.AsSpan();
            var sb = new StringBuilder(CharsPerLine);

            int wordStart = -1;
            for (int i = 0; i <= span.Length; i++)
            {
                // Treat end of span or any whitespace as a word boundary
                bool isBoundary = i == span.Length || char.IsWhiteSpace(span[i]);

                if (isBoundary)
                {
                    if (wordStart != -1)
                    {
                        var word = span.Slice(wordStart, i - wordStart);
                        
                        if (sb.Length > 0)
                        {
                            if (sb.Length + 1 + word.Length <= CharsPerLine)
                            {
                                sb.Append(' ').Append(word);
                            }
                            else
                            {
                                lines.Add(sb.ToString());
                                sb.Clear();
                                sb.Append(word);
                            }
                        }
                        else
                        {
                            sb.Append(word);
                        }
                        wordStart = -1;
                    }
                }
                else if (wordStart == -1)
                {
                    wordStart = i;
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
                Array.Fill(emptyLines, " ");
                Render(emptyLines);
                return;
            }



            var lines = GetLines(textToRender);
            int maxLines = Math.Min(VisibleLines, _setLines.Length);

            string[] displayLines = new string[_setLines.Length];
            Array.Fill(displayLines, "");

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
