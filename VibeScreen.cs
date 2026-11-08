using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Full-screen, user-friendly terminal GUI inspired by mistral-vibe.
/// Layout:
/// - Rows 0..1: Top Header (Logo, model pill, branch, folder)
/// - Rows 2..(H-6): Scrollable transcript viewport
/// - Rows (H-5)..(H-2): Bottom pinned prompt composer box
/// - Rows (H-2): Status footer bar
/// Keystrokes only update the bottom prompt line, NEVER printing duplicate boxes.
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
        Console.Write("\x1b[?1049h\x1b[2J\x1b[H"); // alternate screen, clear, home
        Console.Out.Flush();
        _altScreenActive = true;
    }

    public static void ExitAltScreen()
    {
        if (!_altScreenActive) return;
        Console.Write("\x1b[?1049l\x1b[?25h"); // restore main screen & show cursor
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
        var accent = theme.AccentHex;
        int maxW = Math.Max(30, Console.WindowWidth - 6);

        if (m.Role == "user")
        {
            TranscriptLines.Add("");
            TranscriptLines.Add($"[bold cyan]❯ You[/]");
            foreach (var line in WrapText(m.Content, maxW))
            {
                TranscriptLines.Add($"  [white]{Markup.Escape(line)}[/]");
            }
        }
        else if (m.Role == "assistant")
        {
            TranscriptLines.Add("");
            TranscriptLines.Add($"[bold {accent}]✦ ctv[/]");
            FormatAssistantTranscript(m.Content, maxW, accent);
        }
        else
        {
            TranscriptLines.Add("");
            TranscriptLines.Add($"[dim]ℹ {Markup.Escape(m.Content)}[/]");
        }
    }

    public static void AddToolResult(string title, string content, bool success, AppConfig cfg)
    {
        int maxW = Math.Max(30, Console.WindowWidth - 8);
        var color = success ? "green" : "red";
        var icon = success ? "✓" : "✗";

        TranscriptLines.Add("");
        TranscriptLines.Add($"  [{color}]╭─ {icon} {Markup.Escape(title)} ─[/]");
        foreach (var line in WrapText(content, maxW))
        {
            TranscriptLines.Add($"  [{color}]│[/] [dim]{Markup.Escape(line)}[/]");
        }
        TranscriptLines.Add($"  [{color}]╰───[/]");
    }

    static void FormatAssistantTranscript(string content, int maxW, string accent)
    {
        var lines = content.Split('\n');
        bool inCode = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("```"))
            {
                inCode = !inCode;
                TranscriptLines.Add($"  [dim]{Markup.Escape(line)}[/]");
                continue;
            }

            if (inCode)
            {
                TranscriptLines.Add($"  [dim]│[/] {Markup.Escape(line)}");
            }
            else
            {
                var wrapped = WrapText(line, maxW);
                if (wrapped.Count == 0)
                {
                    TranscriptLines.Add("");
                }
                else
                {
                    foreach (var w in wrapped)
                    {
                        TranscriptLines.Add($"  {Markup.Escape(w)}");
                    }
                }
            }
        }
    }

    static List<string> WrapText(string text, int maxWidth)
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

    // ── Full-Screen Rendering ──────────────────────────────────────────────
    public static void RedrawScreen(AppConfig cfg, string promptText = "", int cursor = 0, string? status = null)
    {
        int W = Math.Max(40, Console.WindowWidth);
        int H = Math.Max(12, Console.WindowHeight);

        var theme = Ui.Get(cfg);
        var accent = theme.AccentHex;
        var active = cfg.Active();

        // 1. Clear screen and home cursor
        Console.Write("\x1b[2J\x1b[H");

        // 2. Render Header (Rows 0 and 1)
        var branch = GetGitBranch();
        var branchText = !string.IsNullOrEmpty(branch) ? $"  {branch}" : "";
        var cwdName = Path.GetFileName(Directory.GetCurrentDirectory());

        var modelName = active is not null ? active.Model ?? active.Name : "no-provider";
        var leftHeader = $" ctv · cool terminal vibe";
        var rightHeader = $"{modelName}{branchText} 📁 {cwdName} ";

        var spaceCount = Math.Max(1, W - leftHeader.Length - rightHeader.Length);
        var headerLine = $"[bold {accent}]{leftHeader}[/]" + new string(' ', spaceCount) + $"[dim]{Markup.Escape(rightHeader)}[/]";
        AnsiConsole.MarkupLine(headerLine);

        var div = new string('─', W);
        AnsiConsole.MarkupLine($"[{accent}]{div}[/]");

        // 3. Render Transcript (Rows 2 .. H-6)
        int transcriptHeight = Math.Max(1, H - 7);
        var visibleLines = TranscriptLines.Count > transcriptHeight
            ? TranscriptLines.Skip(TranscriptLines.Count - transcriptHeight).Take(transcriptHeight)
            : TranscriptLines;

        int writtenRows = 0;
        foreach (var l in visibleLines)
        {
            try { AnsiConsole.MarkupLine(l); }
            catch { Console.WriteLine(l); }
            writtenRows++;
        }

        // Fill empty transcript rows
        while (writtenRows < transcriptHeight)
        {
            Console.WriteLine();
            writtenRows++;
        }

        // 4. Render Composer Box at bottom (Rows H-5 .. H-2)
        RenderPromptBox(cfg, promptText, cursor, status);
    }

    public static void RenderPromptBox(AppConfig cfg, string promptText, int cursor, string? status = null)
    {
        int W = Math.Max(40, Console.WindowWidth);
        int H = Math.Max(12, Console.WindowHeight);

        var theme = Ui.Get(cfg);
        var accent = theme.AccentHex;

        int boxTopRow = H - 5;
        try { Console.SetCursorPosition(0, Math.Max(0, boxTopRow)); }
        catch { }

        // Top border with title & hint
        var title = " Prompt (@ file · ! shell · / cmd) ";
        var topDashCount = Math.Max(1, W - 3 - title.Length);
        AnsiConsole.MarkupLine($"[{accent}]╭─[/][bold {accent}]{Markup.Escape(title)}[/][{accent}]{new string('─', topDashCount)}╮[/]");

        // Input row
        int innerW = Math.Max(10, W - 6);
        string visibleText = promptText.Length > innerW
            ? promptText[(promptText.Length - innerW)..]
            : promptText;

        var textLine = !string.IsNullOrEmpty(status)
            ? $"[{accent}]│[/] [bold {accent}]❯[/] [yellow]{Markup.Escape(status)}[/]"
            : string.IsNullOrEmpty(promptText)
                ? $"[{accent}]│[/] [bold {accent}]❯[/] [dim]Type a message or command...[/]"
                : $"[{accent}]│[/] [bold {accent}]❯[/] [white]{Markup.Escape(visibleText)}[/]";

        // Clear row to edge
        Console.Write("\x1b[2K");
        try { AnsiConsole.Markup(textLine); }
        catch { Console.Write($"│ ❯ {visibleText}"); }

        // Move to right edge and draw border
        try
        {
            Console.SetCursorPosition(W - 1, Console.CursorTop);
            AnsiConsole.MarkupLine($"[{accent}]│[/]");
        }
        catch { Console.WriteLine(); }

        // Bottom border
        var bottomDashCount = Math.Max(1, W - 2);
        AnsiConsole.MarkupLine($"[{accent}]╰{new string('─', bottomDashCount)}╯[/]");

        // Footer status bar
        var autoStatus = cfg.AutoApprove ? "[green]auto-approve: on[/]" : "[dim]auto-approve: off[/]";
        var footer = $" [dim]Enter Send · Shift+Enter Newline · Tab Complete · Ctrl+C Exit[/]   [dim]|[/]   {autoStatus}";
        Console.Write("\x1b[2K");
        try { AnsiConsole.MarkupLine(footer); }
        catch { Console.WriteLine(footer); }

        // Position hardware cursor inside the prompt box
        int cursorCol = Math.Min(W - 3, 4 + (promptText.Length > innerW ? innerW : cursor));
        try { Console.SetCursorPosition(cursorCol, Math.Max(0, boxTopRow + 1)); }
        catch { }
    }

    // ── Single-Line Keystroke Fast Update ──────────────────────────────────
    private static void UpdatePromptOnly(AppConfig cfg, string promptText, int cursor)
    {
        int W = Math.Max(40, Console.WindowWidth);
        int H = Math.Max(12, Console.WindowHeight);
        var theme = Ui.Get(cfg);
        var accent = theme.AccentHex;

        int row = H - 4; // Input line row
        try { Console.SetCursorPosition(0, Math.Max(0, row)); }
        catch { return; }

        int innerW = Math.Max(10, W - 6);
        string visibleText = promptText.Length > innerW
            ? promptText[(promptText.Length - innerW)..]
            : promptText;

        Console.Write("\x1b[2K"); // Erase this single line
        var textLine = string.IsNullOrEmpty(promptText)
            ? $"[{accent}]│[/] [bold {accent}]❯[/] [dim]Type a message or command...[/]"
            : $"[{accent}]│[/] [bold {accent}]❯[/] [white]{Markup.Escape(visibleText)}[/]";

        try { AnsiConsole.Markup(textLine); }
        catch { Console.Write($"│ ❯ {visibleText}"); }

        try
        {
            Console.SetCursorPosition(W - 1, row);
            AnsiConsole.Markup($"[{accent}]│[/]");
        }
        catch { }

        // Move cursor to typing point
        int cursorCol = Math.Min(W - 3, 4 + (promptText.Length > innerW ? innerW : cursor));
        try { Console.SetCursorPosition(cursorCol, row); }
        catch { }
    }

    // ── Interactive Composer ───────────────────────────────────────────────
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

        // Render full screen once at the start of composer
        RedrawScreen(cfg, buf.ToString(), cursor);

        while (true)
        {
            // Detect window resize
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
                return null; // Cancel / exit
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

        // Position modal in lower half above prompt
        int modalRow = Math.Max(2, H - 14);
        try { Console.SetCursorPosition(0, modalRow); }
        catch { }

        var content = new StringBuilder();
        content.AppendLine($"[bold yellow]Target:[/] {Markup.Escape(label)}");
        content.AppendLine();
        content.AppendLine("  [bold cyan]1[/]. Allow once");
        content.AppendLine("  [bold cyan]2[/]. Allow for remainder of this session");
        content.AppendLine("  [bold cyan]3[/]. Always allow this action");
        content.AppendLine("  [bold cyan]4[/]. Always allow everything (auto-mode)");
        content.AppendLine("  [bold red]5[/]. Deny");
        content.AppendLine();
        content.AppendLine("[dim]Press [1-5] for quick select, or Esc to reject[/]");

        var modal = new Panel(new Markup(content.ToString()))
            .Header("[bold yellow]⚠ Tool Approval Required[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("yellow"));

        AnsiConsole.Write(modal);

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

        // Redraw cleanly after decision
        RedrawScreen(cfg);
        return outcome;
    }
}
