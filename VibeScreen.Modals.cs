using System.Text;

namespace Ctv;

public static partial class VibeScreen
{
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

    static void RunModal(Modal m)
    {
        // Keys typed ahead while something else was running must never answer a dialog
        // (a stray Enter could otherwise approve a command).
        while (KeySignal.Wait(0)) KeyQueue.TryDequeue(out _);
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

                var ev = NextKey(80);
                if (ev is null) continue;

                bool done;
                lock (Gate)
                {
                    done = m.HandleKey(ev.Value.Key);
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
    abstract class Modal
    {
        public bool Cancelled;
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

        public string Result = "";

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

    sealed class AskModal : Modal
    {
        readonly string _label;
        readonly bool _secret, _allowEmpty;
        readonly TextBuffer _buf = new();
        string _error = "";

        public string Result = "";

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
}
