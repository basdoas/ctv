using System.Text;

namespace Ctv;

public static partial class VibeScreen
{
    /// <summary>One row of a settings-style menu: name, current value, and an explanation shown while it is highlighted.</summary>
    public sealed record MenuItem(string Label, string Value, string Hint);

    static Modal? _modal;
    static int _modalDepth;

    // ── public dialogs ──────────────────────────────────────────────────────
    /// <summary>Arrow-key list with type-to-filter. Returns null when cancelled.</summary>
    public static string? Select(string title, IReadOnlyList<string> choices)
    {
        if (choices.Count == 0) return null;
        var m = new SelectModal(title, choices);
        RunModal(m);
        return m.Cancelled ? null : m.Result;
    }

    /// <summary>One-line text input (optionally masked). Returns null when cancelled.</summary>
    public static string? Ask(string label, string? defaultValue, bool secret, bool allowEmpty)
    {
        var m = new AskModal(label, defaultValue ?? "", secret, allowEmpty);
        RunModal(m);
        return m.Cancelled ? null : m.Result;
    }

    /// <summary>The permission prompt used by <see cref="SafetyGate"/> before files/shell/git/MCP actions.</summary>
    public static PermissionOutcome ShowApprovalModal(string actionKey, string label, AppConfig cfg)
    {
        lock (Gate) _cfg ??= cfg;
        var m = new ApprovalModal(label);
        RunModal(m);

        switch (m.Cancelled ? 5 : m.Choice)
        {
            case 1:
                return PermissionOutcome.Allow;
            case 2:
                SafetyGate.AllowForSession(actionKey);
                return PermissionOutcome.Allow;
            case 3:
                if (!cfg.AllowedActions.Contains(actionKey, StringComparer.OrdinalIgnoreCase))
                {
                    cfg.AllowedActions.Add(actionKey);
                    ConfigStore.Save(cfg);
                }
                return PermissionOutcome.Allow;
            case 4:
                cfg.AutoApprove = true;
                ConfigStore.Save(cfg);
                return PermissionOutcome.Allow;
            default:
                return PermissionOutcome.Deny;
        }
    }

    /// <summary>A settings-style list (name, current value, hint for the highlighted row). Returns the chosen index, or null on Esc.</summary>
    public static int? Menu(string title, IReadOnlyList<MenuItem> items, int initial = 0)
    {
        if (items.Count == 0) return null;
        var m = new MenuModal(title, items, initial);
        RunModal(m);
        return m.Cancelled ? null : m.Index;
    }

    public static void RunModal(Modal m)
    {
        // Keys typed ahead while something else was running must never answer a dialog
        // (a stray Enter could otherwise approve a command).
        while (EventSignal.Wait(0)) EventQueue.TryDequeue(out _);
        _ctrlCSignal = false;

        Interlocked.Increment(ref _modalDepth);
        lock (Gate)
        {
            _modal = m;
            _dirty = true;
            DrawLocked();
        }

        try
        {
            while (true)
            {
                if (_ctrlCSignal)
                {
                    _ctrlCSignal = false;
                    m.Cancelled = true;
                    break;
                }

                var ev = NextEvent(80);
                if (ev is not KeyEvent keyEvent) continue; // timeouts and mouse reports are ignored inside dialogs

                bool done;
                lock (Gate)
                {
                    done = m.HandleKey(keyEvent.Key);
                    if (!done) DrawLocked();
                }
                if (done) break;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _modalDepth);
            lock (Gate)
            {
                _modal = null;
                _dirty = true;
                DrawLocked();
            }
        }
    }

    // ── dialog widgets ──────────────────────────────────────────────────────
    public abstract class Modal
    {
        public bool Cancelled;
        public string Result = "";
        /// <summary>1-based screen position of the text cursor; row 0 hides it.</summary>
        public int CursorRow;
        public int CursorCol;

        /// <summary>Returns true when the dialog is finished.</summary>
        public abstract bool HandleKey(ConsoleKeyInfo k);
        public abstract void Render(StringBuilder sb, int W, int H, string accent);

        protected static void Top(StringBuilder sb, int row, int col, int boxW, string title, string border)
        {
            string t = AnsiText.Truncate(title, Math.Max(1, boxW - 6));
            int dashes = Math.Max(1, boxW - 5 - AnsiText.Width(t));
            sb.Append($"\x1b[{row};{col}H{border}╭─ {Bold}{t}{Reset}{border} {new string('─', dashes)}╮{Reset}");
        }

        protected static void Line(StringBuilder sb, int row, int col, int boxW, string styled, string border)
        {
            int innerW = boxW - 4;
            int w = AnsiText.VisibleWidth(styled);
            sb.Append($"\x1b[{row};{col}H{border}│{Reset} {styled}{new string(' ', Math.Max(0, innerW - w))} {border}│{Reset}");
        }

        protected static void Bottom(StringBuilder sb, int row, int col, int boxW, string border) =>
            sb.Append($"\x1b[{row};{col}H{border}╰{new string('─', boxW - 2)}╯{Reset}");
    }

    sealed class SelectModal : Modal
    {
        readonly string _title;
        readonly IReadOnlyList<string> _all;
        readonly TextBuffer _filter = new();
        List<int> _idx = new();
        int _sel, _top;

        public SelectModal(string title, IReadOnlyList<string> all)
        {
            _title = title;
            _all = all;
            Refilter();
        }

        void Refilter()
        {
            string f = _filter.Text.Trim();
            _idx = new List<int>();
            for (int i = 0; i < _all.Count; i++)
                if (f.Length == 0 || _all[i].Contains(f, StringComparison.OrdinalIgnoreCase))
                    _idx.Add(i);
            _sel = 0;
            _top = 0;
        }

        void Move(int delta, bool wrap)
        {
            if (_idx.Count == 0) return;
            int next = _sel + delta;
            if (wrap) next = (next % _idx.Count + _idx.Count) % _idx.Count;
            _sel = Math.Clamp(next, 0, _idx.Count - 1);
        }

        public override bool HandleKey(ConsoleKeyInfo k)
        {
            switch (k.Key)
            {
                case ConsoleKey.Enter:
                    if (_idx.Count == 0) return false;
                    Result = _all[_idx[_sel]];
                    return true;
                case ConsoleKey.Escape:
                    if (!_filter.IsEmpty)
                    {
                        _filter.Clear();
                        Refilter();
                        return false;
                    }
                    Cancelled = true;
                    return true;
                case ConsoleKey.UpArrow: Move(-1, true); return false;
                case ConsoleKey.DownArrow: Move(1, true); return false;
                case ConsoleKey.PageUp: Move(-8, false); return false;
                case ConsoleKey.PageDown: Move(8, false); return false;
            }

            if (_filter.IsEmpty)
            {
                // Short lists: press the number to choose.
                if (_all.Count <= 9 && k.KeyChar >= '1' && k.KeyChar <= '9')
                {
                    int n = k.KeyChar - '1';
                    if (n < _idx.Count)
                    {
                        Result = _all[_idx[n]];
                        return true;
                    }
                    return false;
                }
                if (k.Key == ConsoleKey.Home) { _sel = 0; return false; }
                if (k.Key == ConsoleKey.End) { _sel = Math.Max(0, _idx.Count - 1); return false; }
            }

            string before = _filter.Text;
            _filter.HandleKey(k);
            if (_filter.Text != before) Refilter();
            return false;
        }

        public override void Render(StringBuilder sb, int W, int H, string accent)
        {
            int widest = _all.Count == 0 ? 10 : _all.Max(s => AnsiText.Width(s)) + 6;
            int boxW = Math.Clamp(Math.Max(widest, AnsiText.Width(_title) + 8), 36, Math.Max(36, Math.Min(W - 4, 90)));
            int innerW = boxW - 4;
            bool showFilter = _all.Count > 8 || !_filter.IsEmpty;
            int wanted = _idx.Count == 0 ? 1 : _idx.Count;
            int vis = Math.Max(1, Math.Min(Math.Min(wanted, 14), Math.Max(3, H - 10)));
            if (_sel < _top) _top = _sel;
            if (_sel >= _top + vis) _top = _sel - vis + 1;
            _top = Math.Max(0, _top);

            int boxH = vis + 3 + (showFilter ? 1 : 0);
            int row0 = Math.Max(2, (H - boxH) / 2);
            int col0 = Math.Max(1, (W - boxW) / 2 + 1);
            int r = row0;

            Top(sb, r++, col0, boxW, _title, accent);

            if (showFilter)
            {
                string f = AnsiText.Truncate(_filter.Text, Math.Max(1, innerW - 10));
                Line(sb, r, col0, boxW, $"{Dim}search:{Reset} {f}", accent);
                CursorRow = r;
                CursorCol = col0 + 2 + 8 + AnsiText.Width(f);
                r++;
            }
            else
            {
                CursorRow = 0;
            }

            for (int i = 0; i < vis; i++)
            {
                int ix = _top + i;
                if (_idx.Count == 0)
                {
                    Line(sb, r++, col0, boxW, i == 0 ? $"{Dim}no matches{Reset}" : "", accent);
                    continue;
                }
                if (ix >= _idx.Count)
                {
                    Line(sb, r++, col0, boxW, "", accent);
                    continue;
                }

                string num = _all.Count <= 9 && _filter.IsEmpty ? $"{ix + 1}. " : "";
                string label = AnsiText.Truncate(num + _all[_idx[ix]], Math.Max(1, innerW - 2));
                Line(sb, r++, col0, boxW,
                    ix == _sel ? $"{accent}▸ {Bold}{label}{Reset}" : $"  {label}", accent);
            }

            string hint = "↑↓ move · Enter select · Esc " + (_filter.IsEmpty ? "cancel" : "clear filter")
                          + (showFilter ? " · type to filter" : "");
            Line(sb, r++, col0, boxW, $"{Dim}{AnsiText.Truncate(hint, innerW)}{Reset}", accent);
            Bottom(sb, r, col0, boxW, accent);
        }
    }

    sealed class MenuModal : Modal
    {
        readonly string _title;
        readonly IReadOnlyList<MenuItem> _items;
        int _sel, _top;

        public int Index = -1;

        public MenuModal(string title, IReadOnlyList<MenuItem> items, int initial)
        {
            _title = title;
            _items = items;
            _sel = Math.Clamp(initial, 0, Math.Max(0, items.Count - 1));
        }

        public override bool HandleKey(ConsoleKeyInfo k)
        {
            switch (k.Key)
            {
                case ConsoleKey.UpArrow: _sel = (_sel + _items.Count - 1) % _items.Count; return false;
                case ConsoleKey.DownArrow: _sel = (_sel + 1) % _items.Count; return false;
                case ConsoleKey.Home: _sel = 0; return false;
                case ConsoleKey.End: _sel = _items.Count - 1; return false;
                case ConsoleKey.Enter: Index = _sel; return true;
                case ConsoleKey.Escape: Cancelled = true; return true;
            }
            return false;
        }

        public override void Render(StringBuilder sb, int W, int H, string accent)
        {
            CursorRow = 0;
            int labelW = _items.Max(i => AnsiText.Width(i.Label));
            int valueW = Math.Min(34, _items.Max(i => AnsiText.Width(i.Value)));
            int boxW = Math.Clamp(labelW + valueW + 14, 56, Math.Max(56, Math.Min(W - 4, 92)));
            int innerW = boxW - 4;
            int vis = Math.Max(3, Math.Min(_items.Count, H - 13));
            if (_sel < _top) _top = _sel;
            if (_sel >= _top + vis) _top = _sel - vis + 1;
            _top = Math.Max(0, _top);

            int boxH = vis + 7; // top, rows, divider, 3 hint lines, key help, bottom
            int row0 = Math.Max(2, (H - boxH) / 2);
            int col0 = Math.Max(1, (W - boxW) / 2 + 1);
            int r = row0;

            Top(sb, r++, col0, boxW, _title, accent);

            int valueAvail = Math.Max(4, innerW - 2 - labelW - 2);
            for (int i = 0; i < vis; i++)
            {
                int ix = _top + i;
                if (ix >= _items.Count)
                {
                    Line(sb, r++, col0, boxW, "", accent);
                    continue;
                }

                var it = _items[ix];
                string label = it.Label.PadRight(labelW);
                string value = AnsiText.Truncate(it.Value, valueAvail);
                Line(sb, r++, col0, boxW,
                    ix == _sel
                        ? $"{accent}▸ {Bold}{label}{Reset}  {accent}{value}{Reset}"
                        : $"  {label}  {Dim}{value}{Reset}",
                    accent);
            }

            Line(sb, r++, col0, boxW, $"{Dim}{new string('─', innerW)}{Reset}", accent);

            var hint = AnsiText.Wrap(_items[_sel].Hint, innerW);
            if (hint.Count > 3)
            {
                hint = hint.Take(3).ToList();
                hint[2] = AnsiText.Truncate(AnsiText.Strip(hint[2]) + " …", innerW);
            }
            for (int i = 0; i < 3; i++)
                Line(sb, r++, col0, boxW, i < hint.Count ? hint[i] : "", accent);

            Line(sb, r++, col0, boxW, $"{Dim}↑↓ move · Enter change · Esc close{Reset}", accent);
            Bottom(sb, r, col0, boxW, accent);
        }
    }

    sealed class AskModal : Modal
    {
        readonly string _label;
        readonly bool _secret, _allowEmpty;
        readonly TextBuffer _buf = new();
        string _error = "";

        public AskModal(string label, string defaultValue, bool secret, bool allowEmpty)
        {
            _label = label;
            _secret = secret;
            _allowEmpty = allowEmpty;
            _buf.Set(defaultValue.Replace('\n', ' ').Replace("\r", ""));
        }

        public override bool HandleKey(ConsoleKeyInfo k)
        {
            if (k.Key == ConsoleKey.Enter || k.KeyChar == '\n')
            {
                string v = _buf.Text.Trim();
                if (v.Length == 0 && !_allowEmpty)
                {
                    _error = "A value is required.";
                    return false;
                }
                Result = v;
                return true;
            }
            if (k.Key == ConsoleKey.Escape)
            {
                Cancelled = true;
                return true;
            }

            _error = "";
            _buf.HandleKey(k);
            return false;
        }

        public override void Render(StringBuilder sb, int W, int H, string accent)
        {
            int boxW = Math.Clamp(Math.Max(AnsiText.Width(_label) + 10, 64), 40, Math.Max(40, W - 4));
            int innerW = boxW - 4;
            int boxH = 4 + (_error.Length > 0 ? 1 : 0);
            int row0 = Math.Max(2, (H - boxH) / 2);
            int col0 = Math.Max(1, (W - boxW) / 2 + 1);
            int r = row0;

            string shown = _secret ? new string('*', _buf.Length) : _buf.Text;
            int c = Math.Clamp(_buf.Cursor, 0, shown.Length);
            int avail = innerW - 1;
            int start = 0;
            while (start < c && AnsiText.Width(shown.Substring(start, c - start)) > avail - 1) start++;

            var vis = new StringBuilder();
            int vw = 0;
            for (int i = start; i < shown.Length;)
            {
                int cw = AnsiText.CharWidthAt(shown, i, out int len);
                if (vw + cw > avail) break;
                vis.Append(shown, i, len);
                vw += cw;
                i += len;
            }

            Top(sb, r++, col0, boxW, _label, accent);
            CursorRow = r;
            CursorCol = col0 + 2 + AnsiText.Width(shown.Substring(start, c - start));
            Line(sb, r++, col0, boxW, vis.ToString(), accent);
            if (_error.Length > 0) Line(sb, r++, col0, boxW, $"{Red}{_error}{Reset}", accent);
            Line(sb, r++, col0, boxW, $"{Dim}Enter confirm · Esc cancel{Reset}", accent);
            Bottom(sb, r, col0, boxW, accent);
        }
    }

    sealed class ApprovalModal : Modal
    {
        static readonly string[] Options =
        {
            "Allow once", "Allow for this session", "Always allow this action", "Auto-approve everything", "Deny",
        };

        readonly string _label;
        readonly long _openedAt = Environment.TickCount64;
        int _sel;

        public int Choice = 5;

        public ApprovalModal(string label) => _label = AnsiText.Clean(label, false);

        public override bool HandleKey(ConsoleKeyInfo k)
        {
            switch (k.Key)
            {
                case ConsoleKey.UpArrow:
                    _sel = (_sel + Options.Length - 1) % Options.Length;
                    return false;
                case ConsoleKey.DownArrow:
                    _sel = (_sel + 1) % Options.Length;
                    return false;
                case ConsoleKey.Enter:
                    if (Environment.TickCount64 - _openedAt < 250) return false; // ignore a leftover Enter
                    Choice = _sel + 1;
                    return true;
                case ConsoleKey.Escape:
                    Cancelled = true;
                    Choice = 5;
                    return true;
            }

            if (k.KeyChar >= '1' && k.KeyChar <= '5') { Choice = k.KeyChar - '0'; return true; }
            if (k.KeyChar is 'y' or 'Y') { Choice = 1; return true; }
            if (k.KeyChar is 'n' or 'N') { Choice = 5; return true; }
            return false;
        }

        public override void Render(StringBuilder sb, int W, int H, string accent)
        {
            int boxW = Math.Clamp(78, 40, Math.Max(40, W - 4));
            int innerW = boxW - 4;

            var wrapped = AnsiText.Wrap(_label, innerW);
            if (wrapped.Count > 5)
            {
                wrapped = wrapped.Take(5).ToList();
                wrapped[4] = AnsiText.Truncate(AnsiText.Strip(wrapped[4]) + " …", innerW);
            }

            int boxH = wrapped.Count + Options.Length + 4;
            int row0 = Math.Max(2, (H - boxH) / 2);
            int col0 = Math.Max(1, (W - boxW) / 2 + 1);
            int r = row0;
            CursorRow = 0;

            Top(sb, r++, col0, boxW, "Approval required", Yellow);
            foreach (var w in wrapped) Line(sb, r++, col0, boxW, $"{Bold}{w}{Reset}", Yellow);
            Line(sb, r++, col0, boxW, "", Yellow);
            for (int i = 0; i < Options.Length; i++)
            {
                string text = $"{i + 1}. {Options[i]}";
                string styled = i == _sel
                    ? $"{Yellow}▸ {Bold}{text}{Reset}"
                    : i == Options.Length - 1 ? $"  {Red}{text}{Reset}" : $"  {text}";
                Line(sb, r++, col0, boxW, styled, Yellow);
            }
            Line(sb, r++, col0, boxW, $"{Dim}1-5 choose · Enter confirm · Esc deny{Reset}", Yellow);
            Bottom(sb, r, col0, boxW, Yellow);
        }
    }

    public sealed class ArtifactModal : Modal
    {
        readonly List<(string Lang, string Code)> _blocks;
        int _idx;
        int _scroll;
        public int SelectedIndex => _idx;
        public ArtifactModal(List<(string Lang, string Code)> blocks)
        {
            _blocks = blocks;
        }

        public override bool HandleKey(ConsoleKeyInfo k)
        {
            switch (k.Key)
            {
                case ConsoleKey.Escape:
                    Cancelled = true;
                    return true;
                case ConsoleKey.RightArrow:
                case ConsoleKey.PageDown:
                    _idx = Math.Min(_idx + 1, _blocks.Count - 1);
                    _scroll = 0;
                    return false;
                case ConsoleKey.LeftArrow:
                case ConsoleKey.PageUp:
                    _idx = Math.Max(_idx - 1, 0);
                    _scroll = 0;
                    return false;
                case ConsoleKey.UpArrow:
                    _scroll = Math.Max(0, _scroll - 1);
                    return false;
                case ConsoleKey.DownArrow:
                    _scroll = Math.Min(_scroll + 1, Math.Max(0, _blocks[_idx].Code.Split('\n').Length - 1));
                    return false;
                case ConsoleKey.Enter:
                    Result = _blocks[_idx].Code;
                    return true;
            }
            return false;
        }

        public override void Render(StringBuilder sb, int W, int H, string accent)
        {
            var (lang, code) = _blocks[_idx];
            var lines = code.Split('\n');
            int totalLines = lines.Length;
            int visibleLines = Math.Max(1, H - 12);
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, totalLines - visibleLines));

            int boxW = Math.Clamp(80, 40, Math.Max(40, W - 4));
            int innerW = boxW - 4;
            int boxH = visibleLines + 6;
            int row0 = Math.Max(2, (H - boxH) / 2);
            int col0 = Math.Max(1, (W - boxW) / 2 + 1);
            int r = row0;

            Top(sb, r++, col0, boxW, $"Artifact {_idx + 1}/{_blocks.Count} · {lang}", accent);
            Line(sb, r++, col0, boxW, $"{Dim}◀◀ prev · ▶▶ next · ▲▼ scroll · Enter copy · Esc close{Reset}", accent);
            Line(sb, r++, col0, boxW, "", accent);

            for (int i = _scroll; i < _scroll + visibleLines && i < totalLines; i++)
            {
                string line = lines[i];
                string styled = AnsiText.Truncate(line, Math.Max(1, innerW - 2));
                Line(sb, r++, col0, boxW, $"  {styled}", accent);
            }

            Bottom(sb, r, col0, boxW, accent);
        }
    }
}
