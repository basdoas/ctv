using System.Text;

namespace Ctv;

/// <summary>
/// Small readline-style editor. Works with either Backspace byte (^H or DEL), plus
/// Delete, arrows, Home/End, Ctrl+A/E/U/K/W, word jumps and an Up/Down history.
/// </summary>
public sealed class LineEditor
{
    readonly List<string> history = new();

    /// <summary>Reads one line. Returns null on Ctrl+D with an empty line. Pass mask for secrets.</summary>
    public string? ReadLine(char? mask = null)
    {
        if (Console.IsInputRedirected) return Console.ReadLine();

        var buf = new StringBuilder();
        int pos = 0, prevLen = 0, histIdx = history.Count;
        string draft = "";
        int startLeft = Console.CursorLeft, startTop = Console.CursorTop;

        void Redraw()
        {
            int w = Math.Max(1, Console.BufferWidth);
            string text = mask is { } m ? new string(m, buf.Length) : buf.ToString();
            int total = Math.Max(text.Length, prevLen);
            try
            {
                Console.SetCursorPosition(startLeft, startTop);
                Console.Write(text);
                if (prevLen > text.Length) Console.Write(new string(' ', prevLen - text.Length));
                // The terminal may have scrolled while writing: re-derive where the input started.
                startTop = Math.Max(0, Console.CursorTop - (startLeft + total) / w);
                prevLen = text.Length;
                Console.SetCursorPosition((startLeft + pos) % w, startTop + (startLeft + pos) / w);
            }
            catch (ArgumentOutOfRangeException) { /* odd terminal geometry: ignore this frame */ }
        }

        int WordStart(int p)
        {
            while (p > 0 && buf[p - 1] == ' ') p--;
            while (p > 0 && buf[p - 1] != ' ') p--;
            return p;
        }

        int WordEnd(int p)
        {
            while (p < buf.Length && buf[p] == ' ') p++;
            while (p < buf.Length && buf[p] != ' ') p++;
            return p;
        }

        void DeleteRange(int from, int to)
        {
            if (to > from) { buf.Remove(from, to - from); pos = from; }
        }

        void Set(string s)
        {
            buf.Clear();
            buf.Append(s);
            pos = buf.Length;
        }

        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
            bool alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;

            // Raw Backspace bytes: 0x7f (DEL) = delete char, 0x08 (^H, usually Ctrl+Backspace) = delete word.
            if (k.KeyChar == '\x7f')
            {
                DeleteRange(Math.Max(0, pos - 1), pos);
                Redraw();
                continue;
            }
            if (k.KeyChar == '\b' && k.Key != ConsoleKey.Backspace)
            {
                DeleteRange(WordStart(pos), pos);
                Redraw();
                continue;
            }

            switch (k.Key)
            {
                case ConsoleKey.Enter:
                    pos = buf.Length;
                    Redraw();
                    Console.WriteLine();
                    var line = buf.ToString();
                    if (mask is null && line.Trim().Length > 0 && (history.Count == 0 || history[^1] != line))
                        history.Add(line);
                    return line;

                case ConsoleKey.Backspace:
                    if (pos > 0) DeleteRange(ctrl || alt ? WordStart(pos) : pos - 1, pos);
                    break;

                case ConsoleKey.Delete:
                    if (pos < buf.Length) DeleteRange(pos, ctrl || alt ? WordEnd(pos) : pos + 1);
                    break;

                case ConsoleKey.LeftArrow:
                    pos = ctrl || alt ? WordStart(pos) : Math.Max(0, pos - 1);
                    break;

                case ConsoleKey.RightArrow:
                    pos = ctrl || alt ? WordEnd(pos) : Math.Min(buf.Length, pos + 1);
                    break;

                case ConsoleKey.Home:
                    pos = 0;
                    break;

                case ConsoleKey.End:
                    pos = buf.Length;
                    break;

                case ConsoleKey.UpArrow:
                    if (mask is null && histIdx > 0)
                    {
                        if (histIdx == history.Count) draft = buf.ToString();
                        histIdx--;
                        Set(history[histIdx]);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (mask is null && histIdx < history.Count)
                    {
                        histIdx++;
                        Set(histIdx == history.Count ? draft : history[histIdx]);
                    }
                    break;

                case ConsoleKey.Escape:
                    buf.Clear();
                    pos = 0;
                    break;

                case ConsoleKey.Tab:
                    break;

                default:
                    if (ctrl)
                    {
                        switch (k.Key)
                        {
                            case ConsoleKey.A: pos = 0; break;
                            case ConsoleKey.E: pos = buf.Length; break;
                            case ConsoleKey.U: DeleteRange(0, pos); break;
                            case ConsoleKey.K: DeleteRange(pos, buf.Length); break;
                            case ConsoleKey.W: DeleteRange(WordStart(pos), pos); break;
                            case ConsoleKey.D:
                                if (buf.Length == 0) { Console.WriteLine(); return null; }
                                if (pos < buf.Length) DeleteRange(pos, pos + 1);
                                break;
                        }
                    }
                    else if (!char.IsControl(k.KeyChar))
                    {
                        buf.Insert(pos, k.KeyChar);
                        pos++;
                    }
                    break;
            }

            Redraw();
        }
    }
}
