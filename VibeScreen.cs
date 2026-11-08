using System.Diagnostics;
using System.Text;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Full-screen terminal UI (alternate screen buffer) in the spirit of mistral-vibe.
///
/// One owner for the whole screen:
///   * the transcript is a list of entries that is re-wrapped for the current width on every paint
///     (so resizing works and long lines can't break the layout) and can be scrolled;
///   * a repaint thread animates the spinner, follows streaming output and reacts to resizes;
///   * a key-pump thread is the only reader of the keyboard, so the composer, dialogs and the
///     "Esc to cancel" shortcut never fight over input;
///   * legacy code that prints with Spectre / Console.WriteLine is redirected into the transcript
///     with <see cref="Capture"/>, and its prompts are replaced by native dialogs
///     (<see cref="Select"/>, <see cref="Ask"/>, <see cref="ShowApprovalModal"/>).
///
/// Layout:  row 1 header · row 2 gradient divider · transcript · prompt box (1-6 rows) · footer.
/// </summary>
public static partial class VibeScreen
{
    const string Reset = "\x1b[0m";
    const string Dim = "\x1b[2m";
    const string Bold = "\x1b[1m";
    const string Cyan = "\x1b[36;1m";
    const string Yellow = "\x1b[33m";
    const string Green = "\x1b[32m";
    const string Red = "\x1b[31m";

    const int MaxComposerRows = 6;
    static readonly string[] Spin = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    // ── shared state (everything below is guarded by Gate) ──────────────────
    static readonly object Gate = new();
    static readonly List<TranscriptEntry> _entries = new();
    static readonly TextBuffer _composer = new();
    static TextWriter Out = Console.Out;
    static bool _alt;
    static AppConfig? _cfg;
    static bool _dirty;
    static bool _forceClear;
    static readonly bool _trueColor = DetectTrueColor();
    static int _lastW, _lastH;

    static bool _follow = true;   // transcript sticks to the bottom
    static int _topLine;          // first visible transcript line when not following
    static int _lastMaxTop;       // largest valid _topLine at the last paint

    static volatile bool _busy;
    static string _busyText = "";
    static long _busyStart;
    static int _frame;
    static string _flash = "";
    static long _flashUntil;

    static bool _composerActive;
    static int _composerScroll;
    static int _layoutW = 40;
    static List<(int Start, int Len)> _rows = new() { (0, 0) };

    static string _gradKey = "";
    static string _gradLine = "";
    static string _branch = "";
    static long _branchAt;

    static Thread? _ticker;
    static Thread? _pump;

    /// <summary>True while the full-screen UI owns the terminal.</summary>
    public static bool Active => _alt;

    /// <summary>Called when the user presses Esc while the app is busy (cancel the running request).</summary>
    public static Action? CancelAction { get; set; }

    // ── colors ──────────────────────────────────────────────────────────────
    static bool DetectTrueColor()
    {
        var ct = Environment.GetEnvironmentVariable("COLORTERM") ?? "";
        if (ct.Contains("truecolor", StringComparison.OrdinalIgnoreCase) ||
            ct.Contains("24bit", StringComparison.OrdinalIgnoreCase)) return true;
        var term = Environment.GetEnvironmentVariable("TERM") ?? "";
        return term.Contains("direct", StringComparison.OrdinalIgnoreCase);
    }

    static string Fg((int R, int G, int B) c)
    {
        if (_trueColor) return $"\x1b[38;2;{c.R};{c.G};{c.B}m";
        static int Q(int v) => v < 48 ? 0 : v < 115 ? 1 : (v - 35) / 40;
        return $"\x1b[38;5;{16 + 36 * Q(c.R) + 6 * Q(c.G) + Q(c.B)}m";
    }

    static string Gradient(int w, Theme t)
    {
        string key = $"{w}|{t.Name}|{_trueColor}";
        if (key == _gradKey) return _gradLine;
        var sb = new StringBuilder();
        for (int i = 0; i < w; i++)
        {
            double f = w <= 1 ? 0 : (double)i / (w - 1);
            var c = ((int)(t.From.R + (t.To.R - t.From.R) * f),
                     (int)(t.From.G + (t.To.G - t.From.G) * f),
                     (int)(t.From.B + (t.To.B - t.From.B) * f));
            sb.Append(Fg(c)).Append('─');
        }
        sb.Append(Reset);
        _gradKey = key;
        _gradLine = sb.ToString();
        return _gradLine;
    }

    // ── alternate screen lifecycle ──────────────────────────────────────────
    public static void EnterAltScreen()
    {
        lock (Gate)
        {
            if (_alt) return;
            Out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16) { AutoFlush = false };
            _alt = true;
            _forceClear = true;
            Out.Write("\x1b[?1049h\x1b[2J\x1b[H");
            Out.Flush();
        }
        StartThreads();
    }

    public static void ExitAltScreen()
    {
        bool taken = false;
        try
        {
            Monitor.TryEnter(Gate, 500, ref taken); // never hang process exit on a stuck repaint
            if (!_alt) return;
            _alt = false;
            Out.Write("\x1b[?2026l\x1b[?25h\x1b[?1049l");
            Out.Flush();
        }
        catch { /* terminal already gone */ }
        finally
        {
            if (taken) Monitor.Exit(Gate);
        }
    }

    static void StartThreads()
    {
        if (_ticker is null)
        {
            _ticker = new Thread(TickerLoop) { IsBackground = true, Name = "ctv-paint" };
            _ticker.Start();
        }
        if (_pump is null)
        {
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "ctv-keys" };
            _pump.Start();
        }
    }

    static int SafeW()
    {
        try { var w = Console.WindowWidth; return w > 0 ? w : 80; } catch { return 80; }
    }

    static int SafeH()
    {
        try { var h = Console.WindowHeight; return h > 0 ? h : 24; } catch { return 24; }
    }

    static void TickerLoop()
    {
        while (true)
        {
            Thread.Sleep(_busy ? 70 : 150);
            try
            {
                if (!_alt) continue;
                int w = SafeW(), h = SafeH();
                lock (Gate)
                {
                    bool redraw = _dirty;
                    if (w != _lastW || h != _lastH)
                    {
                        _lastW = w;
                        _lastH = h;
                        foreach (var e in _entries) e.CacheW = -1;
                        _forceClear = true;
                        redraw = true;
                    }
                    if (_busy) { _frame++; redraw = true; }
                    if (_flashUntil != 0 && Environment.TickCount64 > _flashUntil)
                    {
                        _flash = "";
                        _flashUntil = 0;
                        redraw = true;
                    }
                    if (redraw) DrawLocked();
                }
            }
            catch
            {
                // The repaint thread must never die.
            }
        }
    }

    // ── public status helpers ───────────────────────────────────────────────
    /// <summary>Shows a spinner + label in the prompt box (and enables Esc-to-cancel). Pass null to clear.</summary>
    public static void SetBusy(string? text)
    {
        lock (Gate)
        {
            if (text is null) _busy = false;
            else
            {
                if (!_busy) _busyStart = Stopwatch.GetTimestamp();
                _busyText = text;
                _busy = true;
            }
            _dirty = true;
        }
    }

    /// <summary>Shows a short message in the footer for a moment.</summary>
    public static void Flash(string text, int ms = 2500)
    {
        lock (Gate)
        {
            _flash = text;
            _flashUntil = Environment.TickCount64 + ms;
            _dirty = true;
        }
    }

    /// <summary>Kept for compatibility: repaint now (optionally switching the busy label).</summary>
    public static void RedrawScreen(AppConfig cfg, string promptText = "", int cursor = 0, string? status = null)
    {
        lock (Gate)
        {
            _cfg = cfg;
            if (status is not null) SetBusy(status);
            DrawLocked();
        }
    }

    static void DrawNow()
    {
        lock (Gate) DrawLocked();
    }

    public static string GetGitBranch()
    {
        try
        {
            var headFile = Path.Combine(Directory.GetCurrentDirectory(), ".git", "HEAD");
            if (File.Exists(headFile))
            {
                var text = File.ReadAllText(headFile).Trim();
                if (text.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                    return text["ref: refs/heads/".Length..];
                return text.Length > 7 ? text[..7] : text;
            }
        }
        catch { }
        return "";
    }

    static string CachedBranch()
    {
        long now = Environment.TickCount64;
        if (now - _branchAt > 2000)
        {
            _branch = GetGitBranch();
            _branchAt = now;
        }
        return _branch;
    }

    // ── painting ────────────────────────────────────────────────────────────
    static void DrawLocked()
    {
        var cfg = _cfg;
        if (!_alt || cfg is null) return;
        _dirty = false;

        int W = Math.Max(40, SafeW());
        int H = Math.Max(12, SafeH());
        var theme = Ui.Get(cfg);
        string accent = Fg(theme.From);

        var sb = new StringBuilder(16 * 1024);
        sb.Append("\x1b[?2026h\x1b[?25l"); // synchronized update + hide cursor while painting
        if (_forceClear)
        {
            sb.Append("\x1b[2J");
            _forceClear = false;
        }

        // ── row 1: header ──
        var active = cfg.Active();
        string model = active is null ? "no provider" : (string.IsNullOrEmpty(active.Model) ? active.Name : active.Model);
        string branch = CachedBranch();
        string cwd = Path.GetFileName(Directory.GetCurrentDirectory());
        string left = " ✦ ctv";
        string leftTail = " · cool terminal vibe";
        string right = $"{model}{(branch.Length > 0 ? "  git:" + branch : "")}  ▸ {cwd} ";
        int room = W - 1 - AnsiText.Width(left) - AnsiText.Width(leftTail);
        if (room < 12)
        {
            leftTail = "";
            room = W - 1 - AnsiText.Width(left);
        }
        right = AnsiText.Truncate(right, Math.Max(0, room));
        int gap = Math.Max(1, W - 1 - AnsiText.Width(left) - AnsiText.Width(leftTail) - AnsiText.Width(right));
        sb.Append($"\x1b[1;1H{accent}{Bold}{left}{Reset}{Dim}{leftTail}{Reset}{new string(' ', gap)}{Dim}{right}{Reset}\x1b[K");

        // ── row 2: gradient divider ──
        sb.Append($"\x1b[2;1H{Gradient(W, theme)}");

        // ── composer layout (decides how tall the prompt box is) ──
        int innerW = W - 6;
        int layoutW = Math.Max(5, innerW - 1);
        string text = _composer.Text;
        var crows = LayoutRows(text, layoutW);
        int visRows = Math.Min(Math.Max(1, crows.Count), MaxComposerRows);
        var (curRow, curCol) = CursorPos(crows, text, _composer.Cursor);
        if (curRow < _composerScroll) _composerScroll = curRow;
        if (curRow >= _composerScroll + visRows) _composerScroll = curRow - visRows + 1;
        _composerScroll = Math.Clamp(_composerScroll, 0, Math.Max(0, crows.Count - visRows));
        _layoutW = layoutW;
        _rows = crows;
        int boxH = visRows + 2;
        int tHeight = Math.Max(1, H - boxH - 3);

        // ── transcript ──
        int cw = Math.Max(20, W - 2);
        var all = new List<string>(256);
        foreach (var e in _entries) all.AddRange(LinesFor(e, cw, theme));
        int maxTop = Math.Max(0, all.Count - tHeight);
        _lastMaxTop = maxTop;
        int top;
        if (_follow) top = maxTop;
        else
        {
            _topLine = Math.Clamp(_topLine, 0, maxTop);
            top = _topLine;
            if (top >= maxTop) _follow = true;
        }

        int thumbStart = -1, thumbLen = 0;
        if (all.Count > tHeight)
        {
            thumbLen = Math.Max(1, tHeight * tHeight / all.Count);
            thumbStart = (int)((long)top * (tHeight - thumbLen) / Math.Max(1, maxTop));
        }

        for (int i = 0; i < tHeight; i++)
        {
            int idx = top + i;
            string line = idx < all.Count ? all[idx] : "";
            sb.Append($"\x1b[{3 + i};1H{line}{Reset}\x1b[K");
            if (thumbStart >= 0)
            {
                bool inThumb = i >= thumbStart && i < thumbStart + thumbLen;
                sb.Append($"\x1b[{3 + i};{W}H{(inThumb ? accent + "┃" : Dim + "│")}{Reset}");
            }
        }

        // ── prompt box ──
        int boxTop = 3 + tHeight;
        string title;
        string titleStyle;
        if (_busy)
        {
            long secs = (Stopwatch.GetTimestamp() - _busyStart) / Stopwatch.Frequency;
            title = $" {Spin[_frame % Spin.Length]} {_busyText}… {secs}s · Esc to cancel ";
            titleStyle = Yellow;
        }
        else
        {
            title = " Prompt · @ file · ! shell · / command ";
            titleStyle = Bold;
        }
        int dashes = Math.Max(1, W - 3 - AnsiText.Width(title));
        sb.Append($"\x1b[{boxTop};1H{accent}╭─{Reset}{titleStyle}{title}{Reset}{accent}{new string('─', dashes)}╮{Reset}");

        for (int r = 0; r < visRows; r++)
        {
            int rowIdx = _composerScroll + r;
            int row = boxTop + 1 + r;
            string shown;
            int shownW;
            if (text.Length == 0 && r == 0)
            {
                const string ph = "Ask anything · / for commands · @ to attach a file";
                shown = $"{Dim}{AnsiText.Truncate(ph, innerW)}{Reset}";
                shownW = AnsiText.Width(AnsiText.Truncate(ph, innerW));
            }
            else
            {
                string seg = rowIdx < crows.Count ? text.Substring(crows[rowIdx].Start, crows[rowIdx].Len) : "";
                shown = seg;
                shownW = AnsiText.Width(seg);
            }
            string glyph = rowIdx == 0 ? $"{accent}{Bold}❯{Reset}" : " ";
            sb.Append($"\x1b[{row};1H{accent}│{Reset} {glyph} {shown}{new string(' ', Math.Max(0, innerW - shownW))} {accent}│{Reset}");
        }

        sb.Append($"\x1b[{boxTop + boxH - 1};1H{accent}╰{new string('─', W - 2)}╯{Reset}");

        // ── footer ──
        sb.Append(BuildFooter(cfg, W, H, text));

        // ── modal on top ──
        var modal = _modal;
        if (modal is not null) modal.Render(sb, W, H, accent);

        // ── cursor ──
        if (modal is not null)
        {
            if (modal.CursorRow > 0) sb.Append($"\x1b[{modal.CursorRow};{modal.CursorCol}H\x1b[?25h");
        }
        else if (_composerActive)
        {
            int cr = boxTop + 1 + (curRow - _composerScroll);
            int cc = Math.Min(W - 1, 5 + curCol);
            sb.Append($"\x1b[{cr};{cc}H\x1b[?25h");
        }

        sb.Append("\x1b[?2026l");
        try
        {
            Out.Write(sb.ToString());
            Out.Flush();
        }
        catch (IOException)
        {
            // Terminal went away while we were painting.
        }
    }

    static string BuildFooter(AppConfig cfg, int W, int H, string text)
    {
        if (_flash.Length > 0)
            return $"\x1b[{H};1H {Yellow}{AnsiText.Truncate(_flash, W - 3)}{Reset}\x1b[K";

        string plain;
        if (!_follow)
            plain = " ↑ scrolled · PgDn or Ctrl+End to follow the conversation";
        else if (_composerActive && text.StartsWith('/') && !text.Contains(' ') && !text.Contains('\n'))
        {
            var matches = Commands.Names
                .Where(n => n.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .Take(10).ToList();
            plain = matches.Count > 0
                ? " " + string.Join("  ", matches) + "   (Tab completes)"
                : " no matching command · /help lists them";
        }
        else
            plain = " Enter send · Alt+Enter newline · PgUp/PgDn scroll · Ctrl+C quit";

        string rightPlain = cfg.AutoApprove ? "auto-approve: on" : "auto-approve: off";
        string rightStyled = cfg.AutoApprove ? $"{Green}{rightPlain}{Reset}" : $"{Dim}{rightPlain}{Reset}";
        int rightW = rightPlain.Length;

        int avail = W - 1 - rightW - 2;
        if (avail < 12)
        {
            rightStyled = "";
            rightW = 0;
            avail = W - 2;
        }
        plain = AnsiText.Truncate(plain, avail);
        int pad = Math.Max(1, W - 1 - AnsiText.Width(plain) - rightW - 1);
        return $"\x1b[{H};1H{Dim}{plain}{Reset}{new string(' ', pad)}{rightStyled}\x1b[K";
    }
}
