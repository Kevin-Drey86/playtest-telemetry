using System.Collections.Generic;
using System.Text;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Turns a log message + stack trace into a stable fingerprint: the same bug with different
    /// numbers, IDs or paths maps to the same value; a different call site maps to a different one.
    /// Hand-written scanner (no Regex): predictable, allocation-light and IL2CPP friendly.
    /// </summary>
    internal static class Fingerprinter
    {
        public const int MaxTemplateLength = 512;
        const int MaxInputLength = 1024;
        const int MaxQuotedLength = 300;

        static readonly string[] EnginePrefixes =
        {
            "UnityEngine.", "UnityEditor.", "UnityEngineInternal.", "Unity.", "System.", "Mono.", "Microsoft.",
        };

        public static string Compute(string message, string stackTrace)
        {
            string template = NormalizeMessage(message);
            return Compute(template, TopFrames(stackTrace, 3));
        }

        public static string Compute(string template, IList<string> frames)
        {
            var sb = new StringBuilder(template ?? "");
            if (frames != null)
            {
                for (int i = 0; i < frames.Count; i++) sb.Append('\n').Append(frames[i]);
            }
            return Fnv1a.ToHex(Fnv1a.HashUtf8(sb.ToString()));
        }

        // ---------------------------------------------------------------- message normalization

        /// <summary>Replaces numbers, hex addresses, GUIDs, quoted strings and file paths with placeholders.</summary>
        public static string NormalizeMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            if (message.Length > MaxInputLength) message = message.Substring(0, MaxInputLength);

            int n = message.Length;
            var sb = new StringBuilder(n);
            int i = 0;
            while (i < n)
            {
                char c = message[i];
                char prev = i > 0 ? message[i - 1] : ' ';

                // Quoted string. A single quote only counts when it does not follow a letter/digit (don't).
                if (c == '"' || (c == '\'' && !char.IsLetterOrDigit(prev)))
                {
                    int end = message.IndexOf(c, i + 1);
                    if (end > i && end - i <= MaxQuotedLength)
                    {
                        sb.Append("<str>");
                        i = end + 1;
                        continue;
                    }
                }

                bool boundary = !(char.IsLetterOrDigit(prev) || prev == '_' || prev == '.' || prev == '/' || prev == '\\' || prev == '-');
                if (boundary)
                {
                    if (IsGuidAt(message, i))
                    {
                        sb.Append("<guid>");
                        i += 36;
                        continue;
                    }

                    if (c == '0' && i + 2 < n && (message[i + 1] == 'x' || message[i + 1] == 'X') && IsHex(message[i + 2]))
                    {
                        int j = i + 2;
                        while (j < n && IsHex(message[j])) j++;
                        sb.Append("<hex>");
                        i = j;
                        continue;
                    }

                    int tokenEnd = ScanPathToken(message, i);
                    if (tokenEnd > i)
                    {
                        sb.Append("<path>");
                        i = tokenEnd;
                        continue;
                    }
                }

                if (char.IsDigit(c) && !char.IsLetter(prev))
                {
                    int j = i;
                    while (j < n && char.IsDigit(message[j])) j++;
                    if (j + 1 < n && message[j] == '.' && char.IsDigit(message[j + 1]))
                    {
                        j++;
                        while (j < n && char.IsDigit(message[j])) j++;
                    }
                    sb.Append("<n>");
                    i = j;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            if (sb.Length > MaxTemplateLength) sb.Length = MaxTemplateLength;
            return sb.ToString();
        }

        static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        static bool IsGuidAt(string s, int i)
        {
            if (i + 36 > s.Length) return false;
            for (int k = 0; k < 36; k++)
            {
                char c = s[i + k];
                if (k == 8 || k == 13 || k == 18 || k == 23)
                {
                    if (c != '-') return false;
                }
                else if (!IsHex(c)) return false;
            }
            return i + 36 == s.Length || !(char.IsLetterOrDigit(s[i + 36]) || s[i + 36] == '_');
        }

        static bool IsTokenEnd(char c)
        {
            return char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '(' || c == ')' || c == '[' || c == ']' || c == ',' || c == ';';
        }

        static bool IsSeparator(char c)
        {
            return c == '/' || c == '\\';
        }

        /// <summary>Returns the end index of a path-like token starting at start, or start if it is not a path.</summary>
        static int ScanPathToken(string s, int start)
        {
            int end = start;
            while (end < s.Length && !IsTokenEnd(s[end])) end++;
            // Trailing sentence punctuation is not part of the path.
            while (end > start && (s[end - 1] == '.' || s[end - 1] == ':' || s[end - 1] == '!' || s[end - 1] == '?')) end--;
            int len = end - start;
            if (len < 3) return start;

            // Windows drive: C:\ or C:/
            if (len >= 3 && char.IsLetter(s[start]) && s[start + 1] == ':' && IsSeparator(s[start + 2])) return end;

            int separators = 0;
            int lastSeparator = -1;
            bool onlyDigitsAndSeparators = true;
            for (int k = start; k < end; k++)
            {
                char c = s[k];
                if (IsSeparator(c))
                {
                    separators++;
                    lastSeparator = k;
                }
                else if (!char.IsDigit(c)) onlyDigitsAndSeparators = false;
            }
            if (separators == 0 || onlyDigitsAndSeparators) return start; // "12/05/2024" is not a path

            if (separators >= 2) return end;
            // One separator: only a path if the last segment has a file extension (Assets/Foo.cs).
            int dot = s.LastIndexOf('.', end - 1, end - lastSeparator);
            return dot > lastSeparator ? end : start;
        }

        // ---------------------------------------------------------------- stack frames

        /// <summary>
        /// The first <paramref name="count"/> frames that are not Unity/engine internals, reduced to
        /// "Namespace.Class:Method" (no arguments, offsets, file names or line numbers).
        /// </summary>
        public static List<string> TopFrames(string stackTrace, int count)
        {
            var frames = new List<string>(count);
            if (string.IsNullOrEmpty(stackTrace)) return frames;

            int pos = 0;
            while (pos < stackTrace.Length && frames.Count < count)
            {
                int eol = stackTrace.IndexOf('\n', pos);
                if (eol < 0) eol = stackTrace.Length;
                string frame = ExtractFrame(stackTrace, pos, eol);
                pos = eol + 1;
                if (frame.Length == 0 || IsEngineFrame(frame)) continue;
                frames.Add(frame);
            }
            return frames;
        }

        static string ExtractFrame(string s, int start, int end)
        {
            // trim
            while (start < end && char.IsWhiteSpace(s[start])) start++;
            while (end > start && char.IsWhiteSpace(s[end - 1])) end--;
            if (end - start >= 3 && s[start] == 'a' && s[start + 1] == 't' && s[start + 2] == ' ') start += 3;

            // Name ends at the first '(' (arguments / "(at file:line)" / "(wrapper ...)"), ' [' or ' in '.
            int stop = end;
            for (int k = start; k < end; k++)
            {
                char c = s[k];
                if (c == '(') { stop = k; break; }
                if (c == ' ' && k + 1 < end && (s[k + 1] == '[' || (s[k + 1] == 'i' && k + 3 < end && s[k + 2] == 'n' && s[k + 3] == ' ')))
                {
                    stop = k;
                    break;
                }
            }
            while (stop > start && char.IsWhiteSpace(s[stop - 1])) stop--;
            return stop > start ? s.Substring(start, stop - start) : "";
        }

        static bool IsEngineFrame(string frame)
        {
            for (int i = 0; i < EnginePrefixes.Length; i++)
            {
                if (frame.StartsWith(EnginePrefixes[i], System.StringComparison.Ordinal)) return true;
            }
            return frame.StartsWith("<RuntimeInitializeOnLoadMethod", System.StringComparison.Ordinal);
        }
    }
}
