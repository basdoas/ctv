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
        int pos = 0, prevPos = 0, histIdx = history.Count;
        string draft = "";

        // Flush any output still buffered by Spectre (or any other layer) so the prompt glyph
        // is actually on-screen before we snapshot the cursor column.
        Console.Out.Flush();
        int startLeft = Console.CursorLeft; // column right after the prompt glyph (e.g. 2 for "❯ ")

        // --- ANSI helpers (bypass Console.SetCursorPosition which can desync with Spectre) ---

        // Move cursor to the logical start of the input field (column startLeft, same row).
        // We use relative movement so we don't need to track the absolute row.
        void GoToStart()
        {
            int w = Math.Max(1, Console.BufferWidth);
            // After the previous Redraw the cursor was left at prevPos characters from startLeft.
            // The number of rows it wrapped below the start row:
            int rowsDown = (startLeft + prevPos) / w;
            if (rowsDown > 0)
                Console.Write($"\x1b[{rowsDown}A"); // cursor up
            Console.Write("\r");                     // go to column 0
            if (startLeft > 0)
                Console.Write($"\x1b[{startLeft}C"); // go right to startLeft
        }

        // Place the cursor at logical edit position `pos` within the text,
        // given that the text just written ends at character index `textLen` from startLeft.
        void GoToPos(int textLen)
        {
            int w   = Math.Max(1, Console.BufferWidth);
            int end = startLeft + textLen;
            int cur = startLeft + pos;
            // Row & column of end of text and desired cursor, relative to start row.
            int endRow = end / w, endCol = end % w;
            int curRow = cur / w, curCol = cur % w;
            int rowDiff = endRow - curRow; // how many rows up we must go
            if (rowDiff > 0) Console.Write($"\x1b[{rowDiff}A");
            if (curCol < endCol) Console.Write($"\x1b[{endCol - curCol}D");
            else if (curCol > endCol) Console.Write($"\x1b[{curCol - endCol}C");
        }

        void Redraw()
        {
            string text = mask is { } m ? new string(m, buf.Length) : buf.ToString();
            try
            {
                GoToStart();
                // \x1b[J = erase from cursor to end of screen → wipes old text without needing spaces.
                Console.Write("\x1b[J");
                Console.Write(text);
                GoToPos(text.Length);
                prevPos = pos;
            }
            catch (Exception) { /* ignore on unusual terminals */ }
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
            bool alt  = (k.Modifiers & ConsoleModifiers.Alt)     != 0;

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
                    // Move cursor to end so the submitted text is fully visible, then newline.
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
                    Redraw();
                    continue; // already redrawn, skip the one at the bottom

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
