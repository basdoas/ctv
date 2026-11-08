using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Full-screen, user-friendly terminal GUI inspired by mistral-vibe.
/// Uses fixed ANSI screen-coordinate positioning (\x1b[{row};{col}H) in alternate buffer.
/// Layout:
/// - Row 1: Top header (Brand, Model pill, git branch, current folder)
/// - Row 2: Divider line
/// - Rows 3..(H-4): Transcript area (scrollable message history)
/// - Row (H-3): Prompt box top border ╭─ Prompt ... ╮
/// - Row (H-2): Prompt input line │ ❯ text ... │ (updated IN-PLACE on keystrokes)
/// - Row (H-1): Prompt box bottom border ╰───────────╯
/// - Row H: Status footer bar
/// Keystrokes NEVER scroll the screen or print duplicate boxes.
/// </summary>
public static class VibeScreen
{
    private static bool _altScreenActive;
    private static readonly List<string> InputHistory = new();
    private static readonly List<string> TranscriptLines = new();

    // ── Alternate Screen Buffer ────────────────────────────────────────────
    public static void EnterAltScreen()
    {
        if (_altScreenActive) return;
        // Enter alternate screen, clear, home cursor, hide cursor temporarily
        Console.Write("\x1b[?1049h\x1b[2J\x1b[H\x1b[?25h");
        Console.Out.Flush();
        _altScreenActive = true;
    }

    public static void ExitAltScreen()
    {
        if (!_altScreenActive) return;
        // Exit alternate screen & show cursor
        Console.Write("\x1b[?1049l\x1b[?25h");
        Console.Out.Flush();
        _altScreenActive = false;
    }

    public static string GetGitBranch()
    {
        try
        {
            var headFile = Path.Combine(Directory.GetCurrentDirectory(), ".git", "HEAD");
            if (File.Exists(headFile))
            {
                var text = File.ReadAllText(headFile).Trim();
                if (text.StartsWith("ref: refs/heads/"))
                    return text["ref: refs/heads/".Length..];
                return text.Length > 7 ? text[..7] : text;
            }
        }
        catch { }
        return "";
    }

    // ── Transcript Management ──────────────────────────────────────────────
    public static void ClearTranscript()
    {
        TranscriptLines.Clear();
    }

    public static void AddMessage(ChatMessage m, AppConfig cfg)
    {
        var theme = Ui.Get(cfg);
        int maxW = Math.Max(20, Console.WindowWidth - 6);

        string accent = $"\x1b[38;2;{theme.From.R};{theme.From.G};{theme.From.B}m";
        const string cyan = "\x1b[36;1m";
        const string reset = "\x1b[0m";
        const string dim = "\x1b[2m";
        const string bold = "\x1b[1m";

        if (m.Role == "user")
        {
            TranscriptLines.Add("");
            TranscriptLines.Add($"{cyan}❯ You{reset}");
            foreach (var line in WrapPlain(m.Content, maxW))
            {
                TranscriptLines.Add($"  {line}");
            }
        }
        else if (m.Role == "assistant")
        {
            TranscriptLines.Add("");
            TranscriptLines.Add($"{accent}{bold}✦ ctv{reset}");
            FormatAssistantLines(m.Content, maxW, accent);
        }
        else
        {
            TranscriptLines.Add("");
            TranscriptLines.Add($"{dim}ℹ {m.Content}{reset}");
        }
    }

    static void FormatAssistantLines(string content, int maxW, string accent)
    {
        const string reset = "\x1b[0m";
        const string dim = "\x1b[2m";
        const string yellow = "\x1b[33m";
        const string green = "\x1b[32m";
        const string blue = "\x1b[34m";

        var rawLines = content.Split('\n');
        bool inCode = false;

        foreach (var raw in rawLines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("```"))
            {
                inCode = !inCode;
                TranscriptLines.Add($"  {dim}{line}{reset}");
                continue;
            }

            if (inCode)
            {
                TranscriptLines.Add($"  {dim}│{reset} {line}");
                continue;
            }

            if (line.StartsWith("[Tool Result: bash") || line.StartsWith("<bash>"))
            {
                TranscriptLines.Add($"  {yellow}╭─ ⚙ Shell Action ─{reset}");
                TranscriptLines.Add($"  {yellow}│{reset} {dim}{line}{reset}");
                TranscriptLines.Add($"  {yellow}╰───{reset}");
                continue;
            }

            if (line.StartsWith("[Tool Result: write_file") || line.StartsWith("<write_file"))
            {
                TranscriptLines.Add($"  {green}╭─ ✏ File Write ─{reset}");
                TranscriptLines.Add($"  {green}│{reset} {dim}{line}{reset}");
                TranscriptLines.Add($"  {green}╰───{reset}");
                continue;
            }

            if (line.StartsWith("[Tool Result: read_file") || line.StartsWith("<read_file"))
            {
                TranscriptLines.Add($"  {blue}╭─ 📖 File Read ─{reset}");
                TranscriptLines.Add($"  {blue}│{reset} {dim}{line}{reset}");
                TranscriptLines.Add($"  {blue}╰───{reset}");
                continue;
            }

            var wrapped = WrapPlain(line, maxW);
            if (wrapped.Count == 0)
            {
                TranscriptLines.Add("");
            }
            else
            {
                foreach (var w in wrapped)
                {
                    TranscriptLines.Add($"  {w}");
                }
            }
        }
    }

    static List<string> WrapPlain(string text, int maxWidth)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;

        while (text.Length > maxWidth)
        {
            int split = text.LastIndexOf(' ', maxWidth);
            if (split <= 0) split = maxWidth;
            list.Add(text[..split].TrimEnd());
            text = text[split..].TrimStart();
        }
        if (text.Length > 0) list.Add(text);
        return list;
    }

    // ── Full Screen Redraw ──────────────────────────────────────────────────
    public static void RedrawScreen(AppConfig cfg, string promptText = "", int cursor = 0, string? status = null)
    {
        int W = Math.Max(40, Console.WindowWidth);
        int H = Math.Max(12, Console.WindowHeight);

        var theme = Ui.Get(cfg);
        string accent = $"\x1b[38;2;{theme.From.R};{theme.From.G};{theme.From.B}m";
        const string reset = "\x1b[0m";
        const string dim = "\x1b[2m";
        const string bold = "\x1b[1m";

        var sb = new StringBuilder();

        // 1. Clear screen & home cursor
        sb.Append("\x1b[2J\x1b[1;1H");

        // 2. Row 1: Header bar
        var branch = GetGitBranch();
        var branchText = !string.IsNullOrEmpty(branch) ? $"  {branch}" : "";
        var cwdName = Path.GetFileName(Directory.GetCurrentDirectory());
        var active = cfg.Active();
        var modelName = active is not null ? active.Model ?? active.Name : "no-provider";

        var leftHeader = " ctv · cool terminal vibe";
        var rightHeader = $"{modelName}{branchText} 📁 {cwdName} ";
        int spaceCount = Math.Max(1, W - leftHeader.Length - rightHeader.Length);

        sb.Append($"\x1b[1;1H\x1b[2K{accent}{bold}{leftHeader}{reset}{new string(' ', spaceCount)}{dim}{rightHeader}{reset}");

        // Row 2: Divider
        sb.Append($"\x1b[2;1H\x1b[2K{accent}{new string('─', W)}{reset}");

        // 3. Rows 3..(H-4): Transcript
        int transcriptHeight = Math.Max(1, H - 6);
        var visibleLines = TranscriptLines.Count > transcriptHeight
            ? TranscriptLines.Skip(TranscriptLines.Count - transcriptHeight).Take(transcriptHeight).ToList()
            : TranscriptLines;

        for (int i = 0; i < transcriptHeight; i++)
        {
            int row = 3 + i;
            if (i < visibleLines.Count)
            {
                sb.Append($"\x1b[{row};1H\x1b[2K{visibleLines[i]}");
            }
            else
            {
                sb.Append($"\x1b[{row};1H\x1b[2K");
            }
        }

        // 4. Row H-3: Prompt top border
        int topRow = H - 3;
        var title = " Prompt (@ file · ! shell · / cmd) ";
        int topDash = Math.Max(1, W - 2 - title.Length);
        sb.Append($"\x1b[{topRow};1H\x1b[2K{accent}╭─{bold}{title}{reset}{accent}{new string('─', topDash)}╮{reset}");

        // Row H-2: Prompt line
        int inputRow = H - 2;
        int innerW = Math.Max(10, W - 6);
        string visibleText;
        if (!string.IsNullOrEmpty(status))
        {
            visibleText = $"\x1b[33m{status}\x1b[0m";
            int pad = Math.Max(0, innerW - status.Length);
            sb.Append($"\x1b[{inputRow};1H\x1b[2K{accent}│{reset} {accent}{bold}❯{reset} {visibleText}{new string(' ', pad)}{accent}│{reset}");
        }
        else if (string.IsNullOrEmpty(promptText))
        {
            int pad = Math.Max(0, innerW - 27);
            sb.Append($"\x1b[{inputRow};1H\x1b[2K{accent}│{reset} {accent}{bold}❯{reset} {dim}Type a message or command...{reset}{new string(' ', pad)}{accent}│{reset}");
        }
        else
        {
            string displayed = promptText.Length > innerW ? promptText[^innerW..] : promptText;
            int pad = Math.Max(0, innerW - displayed.Length);
            sb.Append($"\x1b[{inputRow};1H\x1b[2K{accent}│{reset} {accent}{bold}❯{reset} {displayed}{new string(' ', pad)}{accent}│{reset}");
        }

        // Row H-1: Prompt bottom border
        int bottomRow = H - 1;
        sb.Append($"\x1b[{bottomRow};1H\x1b[2K{accent}╰{new string('─', W - 2)}╯{reset}");

        // Row H: Footer bar
        var autoStatus = cfg.AutoApprove ? "\x1b[32mauto-approve: on\x1b[0m" : $"{dim}auto-approve: off{reset}";
        var footer = $" {dim}Enter Send · Shift+Enter Newline · Tab Complete · Ctrl+C Exit{reset}   {dim}|{reset}   {autoStatus}";
        sb.Append($"\x1b[{H};1H\x1b[2K{footer}");

        // Position cursor inside prompt box
        int cursorCol = Math.Min(W - 2, 5 + (promptText.Length > innerW ? innerW : cursor));
        sb.Append($"\x1b[{inputRow};{cursorCol}H");

        Console.Write(sb.ToString());
        Console.Out.Flush();
    }

    // ── Fast In-Place Keystroke Update (Row H-2 Only) ───────────────────────
    private static void UpdatePromptOnly(AppConfig cfg, string promptText, int cursor)
    {
        int W = Math.Max(40, Console.WindowWidth);
        int H = Math.Max(12, Console.WindowHeight);

        var theme = Ui.Get(cfg);
        string accent = $"\x1b[38;2;{theme.From.R};{theme.From.G};{theme.From.B}m";
        const string reset = "\x1b[0m";
        const string dim = "\x1b[2m";
        const string bold = "\x1b[1m";

        int inputRow = H - 2;
        int innerW = Math.Max(10, W - 6);

        var sb = new StringBuilder();
        if (string.IsNullOrEmpty(promptText))
        {
            int pad = Math.Max(0, innerW - 27);
            sb.Append($"\x1b[{inputRow};1H\x1b[2K{accent}│{reset} {accent}{bold}❯{reset} {dim}Type a message or command...{reset}{new string(' ', pad)}{accent}│{reset}");
        }
        else
        {
            string displayed = promptText.Length > innerW ? promptText[^innerW..] : promptText;
            int pad = Math.Max(0, innerW - displayed.Length);
            sb.Append($"\x1b[{inputRow};1H\x1b[2K{accent}│{reset} {accent}{bold}❯{reset} {displayed}{new string(' ', pad)}{accent}│{reset}");
        }

        int cursorCol = Math.Min(W - 2, 5 + (promptText.Length > innerW ? innerW : cursor));
        sb.Append($"\x1b[{inputRow};{cursorCol}H");

        Console.Write(sb.ToString());
        Console.Out.Flush();
    }

    // ── Interactive Composer Loop ──────────────────────────────────────────
    public static string? ReadComposer(AppConfig cfg)
    {
        var buf = new StringBuilder();
        int cursor = 0;
        int histIdx = InputHistory.Count;
        string draft = "";

        int lastW = Console.WindowWidth;
        int lastH = Console.WindowHeight;

        var slashCommands = new[]
        {
            "/help", "/login", "/provider", "/model", "/theme", "/system",
            "/config", "/permissions", "/save", "/load", "/sessions", "/delete-session",
            "/clear", "/quit"
        };

        // Initial full render
        RedrawScreen(cfg, buf.ToString(), cursor);

        while (true)
        {
            if (Console.WindowWidth != lastW || Console.WindowHeight != lastH)
            {
                lastW = Console.WindowWidth;
                lastH = Console.WindowHeight;
                RedrawScreen(cfg, buf.ToString(), cursor);
            }

            var k = Console.ReadKey(intercept: true);
            bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
            bool shift = (k.Modifiers & ConsoleModifiers.Shift) != 0;
            bool alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;

            if (k.Key == ConsoleKey.Enter)
            {
                if (shift || alt)
                {
                    buf.Insert(cursor, '\n');
                    cursor++;
                    UpdatePromptOnly(cfg, buf.ToString(), cursor);
                    continue;
                }

                var line = buf.ToString().Trim();
                if (line.Length > 0)
                {
                    if (InputHistory.Count == 0 || InputHistory[^1] != line)
                        InputHistory.Add(line);
                }
                return line;
            }

            if (k.Key == ConsoleKey.C && ctrl)
            {
                return null;
            }

            if (k.Key == ConsoleKey.D && ctrl)
            {
                if (buf.Length == 0) return "/quit";
            }

            if (k.Key == ConsoleKey.Backspace)
            {
                if (cursor > 0)
                {
                    buf.Remove(cursor - 1, 1);
                    cursor--;
                    UpdatePromptOnly(cfg, buf.ToString(), cursor);
                }
                continue;
            }

            if (k.Key == ConsoleKey.Delete)
            {
                if (cursor < buf.Length)
                {
                    buf.Remove(cursor, 1);
                    UpdatePromptOnly(cfg, buf.ToString(), cursor);
                }
                continue;
            }

            if (k.Key == ConsoleKey.LeftArrow)
            {
                if (cursor > 0)
                {
                    cursor--;
                    UpdatePromptOnly(cfg, buf.ToString(), cursor);
                }
                continue;
            }

            if (k.Key == ConsoleKey.RightArrow)
            {
                if (cursor < buf.Length)
                {
                    cursor++;
                    UpdatePromptOnly(cfg, buf.ToString(), cursor);
                }
                continue;
            }

            if (k.Key == ConsoleKey.Home)
            {
                cursor = 0;
                UpdatePromptOnly(cfg, buf.ToString(), cursor);
                continue;
            }

            if (k.Key == ConsoleKey.End)
            {
                cursor = buf.Length;
                UpdatePromptOnly(cfg, buf.ToString(), cursor);
                continue;
            }

            if (k.Key == ConsoleKey.UpArrow)
            {
                if (histIdx > 0)
                {
                    if (histIdx == InputHistory.Count) draft = buf.ToString();
                    histIdx--;
                    buf.Clear().Append(InputHistory[histIdx]);
                    cursor = buf.Length;
                    UpdatePromptOnly(cfg, buf.ToString(), cursor);
                }
                continue;
            }

            if (k.Key == ConsoleKey.DownArrow)
            {
                if (histIdx < InputHistory.Count)
                {
                    histIdx++;
                    buf.Clear().Append(histIdx == InputHistory.Count ? draft : InputHistory[histIdx]);
                    cursor = buf.Length;
                    UpdatePromptOnly(cfg, buf.ToString(), cursor);
                }
                continue;
            }

            // Tab completion for commands and @files
            if (k.Key == ConsoleKey.Tab)
            {
                var curText = buf.ToString();
                if (curText.StartsWith('/'))
                {
                    var match = slashCommands.FirstOrDefault(c => c.StartsWith(curText, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        buf.Clear().Append(match);
                        cursor = buf.Length;
                        UpdatePromptOnly(cfg, buf.ToString(), cursor);
                    }
                }
                else if (curText.Contains('@'))
                {
                    var atIndex = curText.LastIndexOf('@');
                    var prefix = curText[(atIndex + 1)..];
                    try
                    {
                        var files = Directory.GetFileSystemEntries(".", $"{prefix}*", SearchOption.TopDirectoryOnly);
                        if (files.Length > 0)
                        {
                            var picked = files[0].Replace('\\', '/').TrimStart('.', '/');
                            buf.Remove(atIndex + 1, prefix.Length);
                            buf.Insert(atIndex + 1, picked);
                            cursor = buf.Length;
                            UpdatePromptOnly(cfg, buf.ToString(), cursor);
                        }
                    }
                    catch { }
                }
                continue;
            }

            if (k.Key == ConsoleKey.Escape)
            {
                buf.Clear();
                cursor = 0;
                UpdatePromptOnly(cfg, buf.ToString(), cursor);
                continue;
            }

            if (ctrl && k.Key == ConsoleKey.W)
            {
                while (cursor > 0 && buf[cursor - 1] == ' ') { buf.Remove(cursor - 1, 1); cursor--; }
                while (cursor > 0 && buf[cursor - 1] != ' ') { buf.Remove(cursor - 1, 1); cursor--; }
                UpdatePromptOnly(cfg, buf.ToString(), cursor);
                continue;
            }

            if (!char.IsControl(k.KeyChar))
            {
                buf.Insert(cursor, k.KeyChar);
                cursor++;
                UpdatePromptOnly(cfg, buf.ToString(), cursor);
            }
        }
    }

    // ── Tool Approval Modal ────────────────────────────────────────────────
    public static PermissionOutcome ShowApprovalModal(string actionKey, string label, AppConfig cfg)
    {
        int W = Math.Max(40, Console.WindowWidth);
        int H = Math.Max(12, Console.WindowHeight);

        var theme = Ui.Get(cfg);
        string accent = $"\x1b[38;2;{theme.From.R};{theme.From.G};{theme.From.B}m";
        const string reset = "\x1b[0m";
        const string bold = "\x1b[1m";
        const string cyan = "\x1b[36m";
        const string yellow = "\x1b[33m";
        const string red = "\x1b[31m";
        const string dim = "\x1b[2m";

        int startRow = Math.Max(3, H / 2 - 4);
        int modalW = Math.Min(W - 4, 70);

        string Trunc(string s, int max) => s.Length > max ? s[..(max - 3)] + "..." : s;

        var sb = new StringBuilder();
        sb.Append($"\x1b[{startRow};2H\x1b[2K{yellow}╭─ ⚠ Tool Approval Required {new string('─', Math.Max(1, modalW - 29))}╮{reset}");
        sb.Append($"\x1b[{startRow + 1};2H\x1b[2K{yellow}│{reset} Target: {bold}{Trunc(label, modalW - 12)}{reset}");
        sb.Append($"\x1b[{startRow + 2};2H\x1b[2K{yellow}│{reset}   {cyan}1{reset}. Allow once          {cyan}2{reset}. Allow for session");
        sb.Append($"\x1b[{startRow + 3};2H\x1b[2K{yellow}│{reset}   {cyan}3{reset}. Always allow        {cyan}4{reset}. Auto-mode (all)");
        sb.Append($"\x1b[{startRow + 4};2H\x1b[2K{yellow}│{reset}   {red}5{reset}. Deny");
        sb.Append($"\x1b[{startRow + 5};2H\x1b[2K{yellow}│{reset} {dim}[1-5] Choose option    [Esc] Deny{reset}");
        sb.Append($"\x1b[{startRow + 6};2H\x1b[2K{yellow}╰{new string('─', modalW - 2)}╯{reset}");

        Console.Write(sb.ToString());
        Console.Out.Flush();

        PermissionOutcome outcome;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.D1 || key.KeyChar == '1')
            {
                outcome = PermissionOutcome.Allow;
                break;
            }
            if (key.Key == ConsoleKey.D2 || key.KeyChar == '2')
            {
                outcome = PermissionOutcome.Allow;
                break;
            }
            if (key.Key == ConsoleKey.D3 || key.KeyChar == '3')
            {
                if (!cfg.AllowedActions.Contains(actionKey, StringComparer.OrdinalIgnoreCase))
                {
                    cfg.AllowedActions.Add(actionKey);
                    ConfigStore.Save(cfg);
                }
                outcome = PermissionOutcome.Allow;
                break;
            }
            if (key.Key == ConsoleKey.D4 || key.KeyChar == '4')
            {
                cfg.AutoApprove = true;
                ConfigStore.Save(cfg);
                outcome = PermissionOutcome.Allow;
                break;
            }
            if (key.Key == ConsoleKey.D5 || key.KeyChar == '5' || key.Key == ConsoleKey.Escape || (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C))
            {
                outcome = PermissionOutcome.Deny;
                break;
            }
        }

        RedrawScreen(cfg);
        return outcome;
    }
}
