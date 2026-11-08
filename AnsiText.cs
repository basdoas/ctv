using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ctv;

/// <summary>ANSI-aware measuring, truncating and wrapping used by the full-screen UI.</summary>
public static class AnsiText
{
    static readonly Regex AnsiRegex = new(@"\x1b\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    static readonly Regex TokenRegex = new(
        @"(?<esc>\x1b\[[0-9;?]*[ -/]*[@-~])|(?<sp> +)|(?<word>[^\x1b ]+)",
        RegexOptions.Compiled);

    public static string Strip(string s) => AnsiRegex.Replace(s, "");

    /// <summary>Removes control characters (including ESC) so untrusted text can't inject terminal codes.</summary>
    public static string Clean(string s, bool keepNewlines)
    {
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\n') { if (keepNewlines) sb.Append('\n'); else sb.Append(' '); }
            else if (c == '\t') sb.Append("    ");
            else if (c == '\r') continue;
            else if (char.IsControl(c)) continue;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Terminal cell width of one rune (0 = combining, 2 = wide / emoji, 1 = everything else).</summary>
    public static int RuneWidth(Rune r)
    {
        int v = r.Value;
        if (v < 32 || (v >= 0x7f && v < 0xa0)) return 0;
        var cat = Rune.GetUnicodeCategory(r);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        if (v >= 0x1100 &&
            (v <= 0x115F || v == 0x2329 || v == 0x232A
             || (v >= 0x2E80 && v <= 0xA4CF && v != 0x303F)
             || (v >= 0xAC00 && v <= 0xD7A3)
             || (v >= 0xF900 && v <= 0xFAFF)
             || (v >= 0xFE30 && v <= 0xFE6F)
             || (v >= 0xFF00 && v <= 0xFF60)
             || (v >= 0xFFE0 && v <= 0xFFE6)
             || (v >= 0x1F300 && v <= 0x1F64F)
             || (v >= 0x1F680 && v <= 0x1F6FF)
             || (v >= 0x1F900 && v <= 0x1F9FF)
             || (v >= 0x20000 && v <= 0x3FFFD)))
            return 2;
        return 1;
    }

    /// <summary>Width of the character starting at index i (handles surrogate pairs); len = UTF-16 units used.</summary>
    public static int CharWidthAt(string s, int i, out int len)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            len = 2;
            return RuneWidth(new Rune(c, s[i + 1]));
        }
        len = 1;
        if (char.IsSurrogate(c)) return 1;
        return RuneWidth(new Rune(c));
    }

    public static int Width(string plain)
    {
        int w = 0;
        for (int i = 0; i < plain.Length;)
        {
            w += CharWidthAt(plain, i, out int len);
            i += len;
        }
        return w;
    }

    public static int VisibleWidth(string withAnsi) => Width(Strip(withAnsi));

    /// <summary>Truncates plain text to at most max cells, adding an ellipsis when something was cut.</summary>
    public static string Truncate(string plain, int max)
    {
        if (max <= 0) return "";
        if (Width(plain) <= max) return plain;
        var sb = new StringBuilder();
        int w = 0;
        for (int i = 0; i < plain.Length;)
        {
            int cw = CharWidthAt(plain, i, out int len);
            if (w + cw > max - 1) break;
            sb.Append(plain, i, len);
            w += cw;
            i += len;
        }
        sb.Append('…');
        return sb.ToString();
    }

    /// <summary>
    /// Word-wraps text that may contain SGR color codes. Active styles are re-applied on continuation
    /// lines and closed at every line end, so a wrapped line never bleeds color into its neighbours.
    /// Leading spaces on the first line are kept (code indentation); words longer than a line are split.
    /// </summary>
    public static List<string> Wrap(string s, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        var cur = new StringBuilder();
        var sgr = new StringBuilder();
        int curW = 0;
        bool continuation = false;

        void NewLine()
        {
            if (sgr.Length > 0) cur.Append("\x1b[0m");
            lines.Add(cur.ToString());
            cur.Clear();
            cur.Append(sgr);
            curW = 0;
            continuation = true;
        }

        foreach (Match m in TokenRegex.Matches(s))
        {
            if (m.Groups["esc"].Success)
            {
                var esc = m.Value;
                cur.Append(esc);
                if (esc.EndsWith('m'))
                {
                    if (esc == "\x1b[0m" || esc == "\x1b[m") sgr.Clear();
                    else sgr.Append(esc);
                }
                continue;
            }

            if (m.Groups["sp"].Success)
            {
                int n = m.Length;
                if (curW + n <= width)
                {
                    if (!(continuation && curW == 0)) { cur.Append(m.Value); curW += n; }
                }
                else
                {
                    NewLine(); // the spaces at the break point are dropped
                }
                continue;
            }

            var word = m.Value;
            int w = Width(word);
            if (curW + w <= width) { cur.Append(word); curW += w; continue; }
            if (curW > 0 && w <= width) { NewLine(); cur.Append(word); curW = w; continue; }

            // A single word wider than the whole line: split it by character.
            if (curW > 0) NewLine();
            for (int i = 0; i < word.Length;)
            {
                int cw = CharWidthAt(word, i, out int len);
                if (curW + cw > width && curW > 0) NewLine();
                cur.Append(word, i, len);
                curW += cw;
                i += len;
            }
        }

        if (sgr.Length > 0) cur.Append("\x1b[0m");
        lines.Add(cur.ToString());
        return lines;
    }
}
