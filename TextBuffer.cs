using System.Text;

namespace Ctv;

/// <summary>
/// Editable text with a cursor: the shared engine behind the composer and the input dialogs.
/// Understands both Backspace bytes (DEL and ^H), Delete, arrows, Home/End, word jumps and Ctrl+A/E/U/K/W/D.
/// </summary>
public sealed class TextBuffer
{
    readonly StringBuilder _sb = new();

    public int Cursor { get; private set; }
    public string Text => _sb.ToString();
    public int Length => _sb.Length;
    public bool IsEmpty => _sb.Length == 0;

    public void Set(string s)
    {
        _sb.Clear();
        _sb.Append(s);
        Cursor = _sb.Length;
    }

    public void Clear()
    {
        _sb.Clear();
        Cursor = 0;
    }

    public void SetCursor(int index) => Cursor = Math.Clamp(index, 0, _sb.Length);

    public void Insert(string s)
    {
        if (s.Length == 0) return;
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");
        _sb.Insert(Cursor, s);
        Cursor += s.Length;
    }

    /// <summary>Replaces a range (used by tab completion) and puts the cursor after the new text.</summary>
    public void Replace(int start, int length, string replacement)
    {
        start = Math.Clamp(start, 0, _sb.Length);
        length = Math.Clamp(length, 0, _sb.Length - start);
        _sb.Remove(start, length);
        _sb.Insert(start, replacement);
        Cursor = start + replacement.Length;
    }

    // ── primitive edits ─────────────────────────────────────────────────────
    int PrevIndex(int i)
    {
        if (i <= 0) return 0;
        i--;
        if (i > 0 && char.IsLowSurrogate(_sb[i]) && char.IsHighSurrogate(_sb[i - 1])) i--;
        return i;
    }

    int NextIndex(int i)
    {
        if (i >= _sb.Length) return _sb.Length;
        if (char.IsHighSurrogate(_sb[i]) && i + 1 < _sb.Length && char.IsLowSurrogate(_sb[i + 1])) return i + 2;
        return i + 1;
    }

    public void Backspace()
    {
        int p = PrevIndex(Cursor);
        if (p == Cursor) return;
        _sb.Remove(p, Cursor - p);
        Cursor = p;
    }

    public void Delete()
    {
        int n = NextIndex(Cursor);
        if (n == Cursor) return;
        _sb.Remove(Cursor, n - Cursor);
    }

    int WordStart(int p)
    {
        while (p > 0 && char.IsWhiteSpace(_sb[p - 1])) p--;
        while (p > 0 && !char.IsWhiteSpace(_sb[p - 1])) p--;
        return p;
    }

    int WordEnd(int p)
    {
        while (p < _sb.Length && char.IsWhiteSpace(_sb[p])) p++;
        while (p < _sb.Length && !char.IsWhiteSpace(_sb[p])) p++;
        return p;
    }

    int LineStart(int p)
    {
        while (p > 0 && _sb[p - 1] != '\n') p--;
        return p;
    }

    int LineEnd(int p)
    {
        while (p < _sb.Length && _sb[p] != '\n') p++;
        return p;
    }

    void RemoveRange(int from, int to)
    {
        if (to <= from) return;
        _sb.Remove(from, to - from);
        Cursor = from;
    }

    public void DeleteWordBack() => RemoveRange(WordStart(Cursor), Cursor);
    public void DeleteWordForward() => RemoveRange(Cursor, WordEnd(Cursor));
    public void DeleteToLineStart() => RemoveRange(LineStart(Cursor), Cursor);
    public void DeleteToLineEnd() => RemoveRange(Cursor, LineEnd(Cursor));

    /// <summary>Applies a generic editing or movement key. Returns true when the key was consumed.</summary>
    public bool HandleKey(ConsoleKeyInfo k)
    {
        bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
        bool alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;
        bool word = ctrl || alt;

        // Raw Backspace bytes: 0x7f (DEL) deletes a char, 0x08 (^H, usually Ctrl+Backspace) deletes a word.
        if (k.KeyChar == '\x7f') { Backspace(); return true; }
        if (k.KeyChar == '\b' && k.Key != ConsoleKey.Backspace) { DeleteWordBack(); return true; }

        switch (k.Key)
        {
            case ConsoleKey.Backspace:
                if (word) DeleteWordBack(); else Backspace();
                return true;
            case ConsoleKey.Delete:
                if (word) DeleteWordForward(); else Delete();
                return true;
            case ConsoleKey.LeftArrow:
                Cursor = word ? WordStart(Cursor) : PrevIndex(Cursor);
                return true;
            case ConsoleKey.RightArrow:
                Cursor = word ? WordEnd(Cursor) : NextIndex(Cursor);
                return true;
            case ConsoleKey.Home:
                Cursor = LineStart(Cursor);
                return true;
            case ConsoleKey.End:
                Cursor = LineEnd(Cursor);
                return true;
        }

        if (ctrl && !alt)
        {
            switch (k.Key)
            {
                case ConsoleKey.A: Cursor = LineStart(Cursor); return true;
                case ConsoleKey.E: Cursor = LineEnd(Cursor); return true;
                case ConsoleKey.B: Cursor = PrevIndex(Cursor); return true;
                case ConsoleKey.F: Cursor = NextIndex(Cursor); return true;
                case ConsoleKey.U: DeleteToLineStart(); return true;
                case ConsoleKey.K: DeleteToLineEnd(); return true;
                case ConsoleKey.W: DeleteWordBack(); return true;
                case ConsoleKey.D: Delete(); return true;
            }
            return false;
        }

        // Printable characters. AltGr (Ctrl+Alt on Windows) is allowed; plain Alt+letter is a shortcut, not text.
        bool printable = !char.IsControl(k.KeyChar) && k.KeyChar != '\0';
        if (printable && (!alt || ctrl))
        {
            Insert(k.KeyChar.ToString());
            return true;
        }

        return false;
    }
}
