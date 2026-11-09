using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace Ctv;

public static partial class VibeScreen
{
    readonly record struct KeyEvent(ConsoleKeyInfo Key, long Stamp);
    readonly record struct MouseEvent(int Button, int X, int Y, bool Pressed, bool WheelUp, bool WheelDown);

    static readonly ConcurrentQueue<object> EventQueue = new();
    static readonly SemaphoreSlim EventSignal = new(0);
    static readonly List<string> InputHistory = new();
    static volatile bool _ctrlCSignal;
    static long _lastCtrlC;

    // ── keyboard pump: the ONLY thread that reads the terminal ─────────────
    // Console.ReadKey puts the terminal into non-canonical, no-echo mode and decodes UTF-8, Alt+key, arrows,
    // Home/End/PgUp/PgDn/Delete and a lone Esc for us. (Reading raw bytes through Console.OpenStandardInput
    // does NOT work on a terminal: .NET gives that stream a line reader, so text only arrives after Enter.)
    static void PumpLoop()
    {
        while (true)
        {
            ConsoleKeyInfo k;
            try { k = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { return; } // stdin is not a terminal
            catch (IOException) { return; }

            try
            {
                HandleKey(k);
            }
            catch
            {
                // A odd key must never kill the key pump.
            }
        }
    }

    static void HandleKey(ConsoleKeyInfo k)
    {
        if (k.Key == ConsoleKey.Enter)
        {
            // On Linux the tty turns Enter into LF, so Enter's KeyChar is '\n' there: never treat '\n' as "newline".
            // A typed Enter has no more input right behind it; a pasted line break does, and that one must not send.
            bool shift = (k.Modifiers & ConsoleModifiers.Shift) != 0;
            bool alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;
            bool pasted = false;
            if (!shift && !alt)
            {
                Thread.Sleep(3);
                try { pasted = Console.KeyAvailable; } catch (InvalidOperationException) { }
            }
            Emit(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift || pasted, alt, false));
            return;
        }

        if (k.KeyChar == '\n') // Ctrl+J: an explicit line break
        {
            Emit(new ConsoleKeyInfo('\r', ConsoleKey.Enter, true, false, false));
            return;
        }

        Emit(k);
    }

    static void Emit(ConsoleKeyInfo k)
    {
        // Esc while a request is running cancels it (but not while a dialog is open: there it means "close/deny").
        if (k.Key == ConsoleKey.Escape && k.Modifiers == 0 && _busy && Volatile.Read(ref _modalDepth) == 0)
        {
            CancelAction?.Invoke();
            return;
        }

        EventQueue.Enqueue(new KeyEvent(k, Stopwatch.GetTimestamp()));
        EventSignal.Release();
    }

    static object? NextEvent(int timeoutMs)
    {
        if (!EventSignal.Wait(timeoutMs)) return null;
        return EventQueue.TryDequeue(out var ev) ? ev : null;
    }

    public static void NotifyCtrlC() => _ctrlCSignal = true;

    public static void ForceExit()
    {
        ExitAltScreen();
        Environment.Exit(130);
    }

    // ── composer (the prompt box) ───────────────────────────────────────────
    /// <summary>Reads one message. Returns null when the user quits (Ctrl+C twice).</summary>
    public static string? ReadComposer(AppConfig cfg)
    {
        lock (Gate)
        {
            _cfg = cfg;
            _composer.Clear();
            _composerScroll = 0;
            _composerActive = true;
            _dirty = true;
        }
        StartThreads();
        DrawNow();

        int histIdx = InputHistory.Count;
        string draft = "";
        List<string>? tabMatches = null;
        int tabIdx = -1, tabStart = 0, tabLen = 0;

        try
        {
            while (true)
            {
                if (_ctrlCSignal)
                {
                    _ctrlCSignal = false;
                    long now = Environment.TickCount64;
                    if (now - _lastCtrlC < 2000) return null;
                    _lastCtrlC = now;
                    lock (Gate) _composer.Clear();
                    Flash("Press Ctrl+C again to exit");
                    DrawNow();
                    continue;
                }

                var ev = NextEvent(60);
                if (ev is null) continue;

                if (ev is MouseEvent mouse)
                {
                    if (mouse.WheelUp) ScrollLines(-3);
                    else if (mouse.WheelDown) ScrollLines(3);
                    continue;
                }

                if (ev is not KeyEvent keyEvent) continue;
                var k = keyEvent.Key;
                bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
                bool alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;
                bool shift = (k.Modifiers & ConsoleModifiers.Shift) != 0;

                if (k.Key != ConsoleKey.Tab) tabMatches = null;

                if (k.Key == ConsoleKey.Enter)
                {
                    // Alt+Enter, Ctrl+J, a pasted line break (reported as Shift) or a trailing backslash add a line;
                    // a plain Enter sends the message.
                    bool newline = alt || shift;
                    string current;
                    lock (Gate) current = _composer.Text;

                    if (!newline && current.EndsWith('\\'))
                    {
                        lock (Gate)
                        {
                            _composer.Backspace();
                            _composer.Insert("\n");
                        }
                        DrawNow();
                        continue;
                    }

                    if (newline)
                    {
                        lock (Gate) _composer.Insert("\n");
                        DrawNow();
                        continue;
                    }

                    string submitted = current.Trim();
                    if (submitted.Length > 0 && (InputHistory.Count == 0 || InputHistory[^1] != submitted))
                        InputHistory.Add(submitted);
                    return submitted;
                }

                switch (k.Key)
                {
                    case ConsoleKey.PageUp:
                        ScrollLines(-Math.Max(1, SafeH() - 10));
                        continue;
                    case ConsoleKey.PageDown:
                        ScrollLines(Math.Max(1, SafeH() - 10));
                        continue;
                    case ConsoleKey.End when ctrl:
                        lock (Gate) { _follow = true; _dirty = true; }
                        DrawNow();
                        continue;
                    case ConsoleKey.Home when ctrl:
                        lock (Gate) { _follow = false; _topLine = 0; _dirty = true; }
                        DrawNow();
                        continue;

                    case ConsoleKey.UpArrow:
                        if (ctrl || alt) { ScrollLines(-3); continue; }
                        if (MoveCursorRows(-1)) { DrawNow(); continue; }
                        if (histIdx > 0)
                        {
                            lock (Gate)
                            {
                                if (histIdx == InputHistory.Count) draft = _composer.Text;
                                histIdx--;
                                _composer.Set(InputHistory[histIdx]);
                            }
                            DrawNow();
                        }
                        continue;

                    case ConsoleKey.DownArrow:
                        if (ctrl || alt) { ScrollLines(3); continue; }
                        if (MoveCursorRows(1)) { DrawNow(); continue; }
                        if (histIdx < InputHistory.Count)
                        {
                            lock (Gate)
                            {
                                histIdx++;
                                _composer.Set(histIdx == InputHistory.Count ? draft : InputHistory[histIdx]);
                            }
                            DrawNow();
                        }
                        continue;

                    case ConsoleKey.Tab:
                        lock (Gate)
                        {
                            if (tabMatches is null)
                            {
                                (tabMatches, tabStart, tabLen) = BuildCompletions(_composer.Text, _composer.Cursor);
                                tabIdx = -1;
                            }
                            if (tabMatches is { Count: > 0 })
                            {
                                tabIdx = (tabIdx + 1) % tabMatches.Count;
                                var pick = tabMatches[tabIdx];
                                _composer.Replace(tabStart, tabLen, pick);
                                tabLen = pick.Length;
                            }
                        }
                        DrawNow();
                        continue;

                    case ConsoleKey.Escape:
                        lock (Gate) _composer.Clear();
                        DrawNow();
                        continue;

                    case ConsoleKey.L when ctrl:
                        lock (Gate) _forceClear = true;
                        DrawNow();
                        continue;

                    case ConsoleKey.D when ctrl:
                        bool empty;
                        lock (Gate) empty = _composer.IsEmpty;
                        if (empty) return "/quit";
                        break;
                }

                bool changed;
                lock (Gate) changed = _composer.HandleKey(k);
                if (changed) DrawNow();
            }
        }
        finally
        {
            lock (Gate)
            {
                _composer.Clear();
                _composerActive = false;
                _dirty = true;
            }
        }
    }

    static void ScrollLines(int delta)
    {
        lock (Gate)
        {
            int cur = _follow ? _lastMaxTop : _topLine;
            int next = Math.Clamp(cur + delta, 0, _lastMaxTop);
            _follow = next >= _lastMaxTop;
            _topLine = next;
            _dirty = true;
            DrawLocked();
        }
    }

    // ── composer text layout helpers ────────────────────────────────────────
    static List<(int Start, int Len)> LayoutRows(string text, int width)
    {
        var rows = new List<(int Start, int Len)>();
        int rowStart = 0, w = 0;
        for (int i = 0; i < text.Length;)
        {
            if (text[i] == '\n')
            {
                rows.Add((rowStart, i - rowStart));
                rowStart = i + 1;
                w = 0;
                i++;
                continue;
            }
            int cw = AnsiText.CharWidthAt(text, i, out int len);
            if (w + cw > width && w > 0)
            {
                rows.Add((rowStart, i - rowStart));
                rowStart = i;
                w = 0;
            }
            w += cw;
            i += len;
        }
        rows.Add((rowStart, text.Length - rowStart));
        return rows;
    }

    static (int Row, int Col) CursorPos(List<(int Start, int Len)> rows, string text, int cursor)
    {
        int row = 0;
        for (int r = 0; r < rows.Count; r++)
            if (rows[r].Start <= cursor && cursor <= rows[r].Start + rows[r].Len) row = r;

        int start = rows[row].Start;
        int end = Math.Clamp(cursor, start, text.Length);
        return (row, AnsiText.Width(text.Substring(start, end - start)));
    }

    static bool MoveCursorRows(int dir)
    {
        lock (Gate)
        {
            string text = _composer.Text;
            var (row, col) = CursorPos(_rows, text, _composer.Cursor);
            int target = row + dir;
            if (target < 0 || target >= _rows.Count) return false;

            var (start, len) = _rows[target];
            int w = 0, idx = start;
            while (idx < start + len)
            {
                int cw = AnsiText.CharWidthAt(text, idx, out int l);
                if (w + cw > col) break;
                w += cw;
                idx += l;
            }
            _composer.SetCursor(idx);
            return true;
        }
    }

    // ── tab completion ──────────────────────────────────────────────────────
    static (List<string>? Matches, int Start, int Len) BuildCompletions(string text, int cursor)
    {
        if (text.StartsWith('/') && !text.Contains(' ') && !text.Contains('\n'))
        {
            var cmds = Commands.Names
                .Where(n => n.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return (cmds, 0, text.Length);
        }

        int at = text.Length == 0 ? -1 : text.LastIndexOf('@', Math.Max(0, cursor - 1));
        if (at >= 0 && at < cursor && (at == 0 || char.IsWhiteSpace(text[at - 1])))
        {
            string partial = text.Substring(at + 1, cursor - at - 1);
            if (!partial.Any(char.IsWhiteSpace))
                return (PathMatches(partial), at, cursor - at);
        }
        return (null, 0, 0);
    }

    static List<string> PathMatches(string partial)
    {
        var result = new List<string>();
        try
        {
            string dirText = "";
            string prefix = partial;
            int slash = partial.LastIndexOf('/');
            if (slash >= 0)
            {
                dirText = partial[..(slash + 1)];
                prefix = partial[(slash + 1)..];
            }

            string dirPath = dirText.Length == 0 ? "." : Platform.ResolvePath(dirText);
            if (!Directory.Exists(dirPath)) return result;

            foreach (var entry in Directory.EnumerateFileSystemEntries(dirPath))
            {
                string name = Path.GetFileName(entry);
                if (name.StartsWith('.') && !prefix.StartsWith('.')) continue;
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add("@" + dirText + name + (Directory.Exists(entry) ? "/" : ""));
                if (result.Count >= 60) break;
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // unreadable directory: offer nothing
        }
        return result;
    }
}
