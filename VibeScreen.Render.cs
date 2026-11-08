using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Ctv;

/// <summary>One item of the conversation shown in the transcript (rendered fresh for the current width on every paint).</summary>
public sealed class TranscriptEntry
{
    internal string Kind = "system";   // user | assistant | system | tool | cmd | raw
    internal string Text = "";
    internal List<string> Raw = new(); // pre-formatted (ANSI) lines for "raw" entries
    internal int Dropped;
    internal int Version;
    internal bool Live;
    internal int CacheW = -1;
    internal int CacheVersion = -1;
    internal string CacheTheme = "";
    internal List<string> Cache = new();
}

public static partial class VibeScreen
{
    const char ToolMarker = '\u0001';
    const int MaxRawLines = 500;

    static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex BulletRegex = new(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex RuleRegex = new(@"^(-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);
    static readonly Regex InlineRegex = new(@"\*\*(?<b>[^*\n]+)\*\*|`(?<c>[^`\n]+)`", RegexOptions.Compiled);
    static readonly Regex MarkupTagRegex = new(@"\[/?(green|red|yellow|dim|bold|cyan|blue)\]", RegexOptions.Compiled);

    static readonly Regex WriteBlock = new(@"<write_file\s+path=[""'](?<p>[^""']+)[""']\s*>(?<c>.*?)</write_file>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EditBlock = new(@"<edit_file\s+path=[""'](?<p>[^""']+)[""']\s*>.*?</edit_file>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex BashBlock = new(@"<bash>\s*(?<c>.*?)\s*</bash>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ReadBlock = new(@"<read_file\s+path=[""'](?<p>[^""']+)[""']\s*/?>|<read_file>(?<p2>.*?)</read_file>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AskBlock = new(@"<ask_question\b[^>]*>(?<c>.*?)</ask_question>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex QuestionTag = new(@"<question>(?<t>.*?)</question>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── transcript API ──────────────────────────────────────────────────────
    public static void ClearTranscript()
    {
        lock (Gate)
        {
            _entries.Clear();
            _follow = true;
            _dirty = true;
        }
    }

    /// <summary>Rebuilds the transcript from a chat history (after /load or /clear).</summary>
    public static void Rebuild(IEnumerable<ChatMessage> history, AppConfig cfg)
    {
        lock (Gate)
        {
            _entries.Clear();
            _follow = true;
            _cfg = cfg;
            foreach (var m in history) AddMessageLocked(m);
            _dirty = true;
        }
    }

    public static void AddMessage(ChatMessage m, AppConfig cfg)
    {
        lock (Gate)
        {
            _cfg = cfg;
            AddMessageLocked(m);
            if (m.Role == "user") _follow = true;
            _dirty = true;
        }
    }

    static void AddMessageLocked(ChatMessage m)
    {
        string kind = m.Role switch
        {
            "user" => m.Content.StartsWith("[Tool Result", StringComparison.Ordinal) ? "tool" : "user",
            "assistant" => "assistant",
            _ => "system",
        };
        _entries.Add(new TranscriptEntry { Kind = kind, Text = m.Content });
    }

    /// <summary>Echoes a slash command / shell line the user just entered.</summary>
    public static void AddCommandEcho(string line)
    {
        lock (Gate)
        {
            _entries.Add(new TranscriptEntry { Kind = "cmd", Text = line });
            _follow = true;
            _dirty = true;
        }
    }

    /// <summary>Starts a streaming assistant message. Update it with <see cref="UpdateAssistant"/>.</summary>
    public static TranscriptEntry BeginAssistant(AppConfig cfg)
    {
        lock (Gate)
        {
            _cfg = cfg;
            var e = new TranscriptEntry { Kind = "assistant", Live = true };
            _entries.Add(e);
            _follow = true;
            _dirty = true;
            return e;
        }
    }

    public static void UpdateAssistant(TranscriptEntry e, string text)
    {
        lock (Gate)
        {
            e.Text = text;
            e.Version++;
            _dirty = true; // the repaint thread picks it up within ~70 ms
        }
    }

    /// <summary>Finishes a streaming message; with keep=false the entry is removed (nothing useful was produced).</summary>
    public static void EndAssistant(TranscriptEntry e, bool keep)
    {
        lock (Gate)
        {
            e.Live = false;
            e.Version++;
            if (!keep || e.Text.Length == 0) _entries.Remove(e);
            _dirty = true;
        }
    }

    internal static void AppendRaw(TranscriptEntry e, string line)
    {
        lock (Gate)
        {
            e.Raw.Add(line);
            if (e.Raw.Count > MaxRawLines)
            {
                e.Raw.RemoveRange(0, 100);
                e.Dropped += 100;
            }
            e.Version++;
            _dirty = true;
        }
    }

    // ── capturing output of legacy code (Spectre / Console.WriteLine) ───────
    /// <summary>Runs code that prints with AnsiConsole or Console and shows its output in the transcript.</summary>
    public static void Capture(Action action)
    {
        var restore = BeginCapture();
        try { action(); }
        finally { restore(); }
    }

    public static async Task CaptureAsync(Func<Task> action)
    {
        var restore = BeginCapture();
        try { await action(); }
        finally { restore(); }
    }

    public static async Task<T> CaptureAsync<T>(Func<Task<T>> action)
    {
        var restore = BeginCapture();
        try { return await action(); }
        finally { restore(); }
    }

    static Action BeginCapture()
    {
        var entry = new TranscriptEntry { Kind = "raw" };
        lock (Gate)
        {
            _entries.Add(entry);
            _follow = true;
            _dirty = true;
        }

        var writer = new TranscriptWriter(entry);
        var oldOut = Console.Out;
        var oldAnsi = AnsiConsole.Console;
        Console.SetOut(writer);
        AnsiConsole.Console = CreateCaptureConsole(writer);

        return () =>
        {
            writer.Finish();
            Console.SetOut(oldOut);
            AnsiConsole.Console = oldAnsi;
            lock (Gate)
            {
                if (entry.Raw.Count == 0) _entries.Remove(entry);
                _dirty = true;
            }
        };
    }

    static IAnsiConsole CreateCaptureConsole(TextWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = _trueColor ? ColorSystemSupport.TrueColor : ColorSystemSupport.EightBit,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = Math.Max(30, SafeW() - 4);
        return console;
    }

    /// <summary>A TextWriter that turns every finished line into a transcript line.</summary>
    sealed class TranscriptWriter(TranscriptEntry entry) : TextWriter
    {
        readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\r') return;
            if (value == '\n') { EmitLine(); return; }
            lock (_line) _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            for (int i = 0; i < count; i++) Write(buffer[index + i]);
        }

        void EmitLine()
        {
            string s;
            lock (_line)
            {
                s = _line.ToString();
                _line.Clear();
            }
            AppendRaw(entry, s);
        }

        public void Finish()
        {
            bool pending;
            lock (_line) pending = _line.Length > 0;
            if (pending) EmitLine();
        }
    }

    // ── rendering ───────────────────────────────────────────────────────────
    static List<string> LinesFor(TranscriptEntry e, int cw, Theme theme)
    {
        if (e.CacheW == cw && e.CacheVersion == e.Version && e.CacheTheme == theme.Name) return e.Cache;
        var lines = RenderEntry(e, cw, theme);
        e.Cache = lines;
        e.CacheW = cw;
        e.CacheVersion = e.Version;
        e.CacheTheme = theme.Name;
        return lines;
    }

    static List<string> RenderEntry(TranscriptEntry e, int cw, Theme theme)
    {
        var lines = new List<string>();
        string accent = Fg(theme.From);

        switch (e.Kind)
        {
            case "user":
                lines.Add("");
                lines.Add($"{Cyan}❯ You{Reset}");
                AddBody(lines, e.Text, cw, 14, "  ");
                break;

            case "assistant":
                if (e.Text.Length == 0) break; // nothing to show yet; the prompt box shows the spinner
                lines.Add("");
                lines.Add($"{accent}{Bold}✦ ctv{Reset}");
                RenderAssistantBody(lines, e.Text, e.Live, cw, accent);
                break;

            case "cmd":
                lines.Add("");
                lines.Add($"{Dim}❯ {AnsiText.Truncate(AnsiText.Clean(e.Text, false), Math.Max(4, cw - 3))}{Reset}");
                break;

            case "tool":
                {
                    lines.Add("");
                    lines.Add($"{Dim}▸ tool result{Reset}");
                    string t = MarkupTagRegex.Replace(AnsiText.Clean(e.Text, true), "");
                    var tl = t.Split('\n').Select(s => s.TrimEnd()).Where(s => s.Length > 0).ToList();
                    int shown = Math.Min(tl.Count, 12);
                    for (int i = 0; i < shown; i++)
                    {
                        string l = tl[i];
                        string style = l.StartsWith('✓') ? Green : l.StartsWith('✗') ? Red : Dim;
                        foreach (var w in AnsiText.Wrap(l, cw - 2)) lines.Add($"  {style}{w}{Reset}");
                    }
                    if (tl.Count > shown)
                        lines.Add($"  {Dim}… {tl.Count - shown} more lines (the model sees all of it){Reset}");
                    break;
                }

            case "raw":
                if (e.Dropped > 0) lines.Add($"  {Dim}… {e.Dropped} earlier lines hidden{Reset}");
                foreach (var l in e.Raw)
                    foreach (var w in AnsiText.Wrap(l, cw - 2))
                        lines.Add("  " + w);
                break;

            default: // system
                {
                    lines.Add("");
                    string style = e.Text.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ? Red : Dim;
                    bool first = true;
                    foreach (var part in AnsiText.Clean(e.Text, true).Split('\n'))
                        foreach (var w in AnsiText.Wrap(part, cw - 2))
                        {
                            lines.Add($"{style}{(first ? "· " : "  ")}{w}{Reset}");
                            first = false;
                        }
                    break;
                }
        }
        return lines;
    }

    static void AddBody(List<string> lines, string text, int cw, int maxLines, string indent)
    {
        var parts = AnsiText.Clean(text, true).Split('\n');
        int shown = Math.Min(parts.Length, maxLines);
        for (int i = 0; i < shown; i++)
            foreach (var w in AnsiText.Wrap(parts[i].TrimEnd(), cw - indent.Length))
                lines.Add(indent + w);
        if (parts.Length > shown)
            lines.Add($"{indent}{Dim}… {parts.Length - shown} more lines{Reset}");
    }

    static string Inline(string s) =>
        InlineRegex.Replace(s, m => m.Groups["b"].Success
            ? $"\x1b[1m{m.Groups["b"].Value}\x1b[22m"
            : $"\x1b[38;5;180m{m.Groups["c"].Value}\x1b[39m");

    static string SummarizeToolBlocks(string text, bool live)
    {
        text = WriteBlock.Replace(text, m =>
            $"\n{ToolMarker}write {m.Groups["p"].Value}  ({m.Groups["c"].Value.Trim().Split('\n').Length} lines)\n");
        text = EditBlock.Replace(text, m => $"\n{ToolMarker}edit {m.Groups["p"].Value}\n");
        text = BashBlock.Replace(text, m => $"\n{ToolMarker}run $ {m.Groups["c"].Value.Trim().Split('\n')[0]}\n");
        text = ReadBlock.Replace(text, m =>
            $"\n{ToolMarker}read {(m.Groups["p"].Success ? m.Groups["p"].Value : m.Groups["p2"].Value.Trim())}\n");
        text = AskBlock.Replace(text, m =>
        {
            var q = QuestionTag.Match(m.Groups["c"].Value);
            return $"\n{ToolMarker}ask {(q.Success ? q.Groups["t"].Value.Trim() : "a question")}\n";
        });

        if (live)
        {
            // A tool block that is still streaming in has no closing tag yet: show a stub instead of its raw body.
            foreach (var (open, label) in new[]
                     {
                         ("<write_file", "writing a file"), ("<edit_file", "editing a file"),
                         ("<bash>", "preparing a command"), ("<ask_question", "preparing a question"),
                     })
            {
                int idx = text.LastIndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) text = text[..idx] + $"\n{ToolMarker}{label}…\n";
            }
        }
        return text;
    }

    static void RenderAssistantBody(List<string> lines, string content, bool live, int cw, string accent)
    {
        string text = SummarizeToolBlocks(AnsiText.Clean(content, true), live);
        bool inCode = false;

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd();
            string trimmed = line.TrimStart();

            if (line.Length > 0 && line[0] == ToolMarker)
            {
                string body = line[1..];
                int sp = body.IndexOf(' ');
                string kind = sp > 0 ? body[..sp] : body;
                string rest = sp > 0 ? body[(sp + 1)..].Trim() : "";
                var wr = AnsiText.Wrap(rest, Math.Max(10, cw - 6 - AnsiText.Width(kind)));
                for (int i = 0; i < wr.Count; i++)
                {
                    lines.Add(i == 0
                        ? $"  {Yellow}▸{Reset} {Bold}{kind}{Reset} {Dim}{wr[i]}{Reset}"
                        : $"    {new string(' ', AnsiText.Width(kind) + 1)}{Dim}{wr[i]}{Reset}");
                }
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCode)
                {
                    inCode = true;
                    string lang = trimmed[3..].Trim();
                    lines.Add($"  {Dim}╭─ {(lang.Length > 0 ? lang : "code")}{Reset}");
                }
                else
                {
                    inCode = false;
                    lines.Add($"  {Dim}╰─{Reset}");
                }
                continue;
            }

            if (inCode)
            {
                foreach (var w in AnsiText.Wrap(line, cw - 4))
                    lines.Add($"  {Dim}│{Reset} \x1b[38;5;252m{w}{Reset}");
                continue;
            }

            if (line.Length == 0) { lines.Add(""); continue; }

            var h = HeadingRegex.Match(line);
            if (h.Success)
            {
                foreach (var w in AnsiText.Wrap(h.Groups[2].Value, cw - 2))
                    lines.Add($"  {accent}{Bold}{w}{Reset}");
                continue;
            }

            if (RuleRegex.IsMatch(trimmed))
            {
                lines.Add($"  {Dim}{new string('─', Math.Max(4, cw - 4))}{Reset}");
                continue;
            }

            var b = BulletRegex.Match(line);
            if (b.Success)
            {
                string indent = new string(' ', Math.Min(b.Groups[1].Length, 8));
                string marker = b.Groups[2].Value;
                string bullet = char.IsDigit(marker[0]) ? marker : "•";
                int hang = indent.Length + AnsiText.Width(bullet) + 1;
                var wr = AnsiText.Wrap(Inline(b.Groups[3].Value), cw - 2 - hang);
                for (int i = 0; i < wr.Count; i++)
                    lines.Add(i == 0
                        ? $"  {indent}{accent}{bullet}{Reset} {wr[i]}"
                        : $"  {new string(' ', hang)}{wr[i]}");
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                foreach (var w in AnsiText.Wrap(trimmed.TrimStart('>').Trim(), cw - 4))
                    lines.Add($"  {Dim}▎ {w}{Reset}");
                continue;
            }

            foreach (var w in AnsiText.Wrap(Inline(line), cw - 2))
                lines.Add($"  {w}");
        }

        if (inCode && !live) lines.Add($"  {Dim}╰─{Reset}");
    }
}
