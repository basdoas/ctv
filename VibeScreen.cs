using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Full-screen, user-friendly terminal GUI inspired by mistral-vibe.
/// Features:
/// - Alternate screen buffer with clean entry and exit.
/// - Top header with gradient logo, model/provider badge, git branch, and session info.
/// - Scrollable transcript with styled user bubbles, assistant markdown, and tool execution cards.
/// - Multi-line composer box with history, @file and /command tab-completion.
/// - High-visibility approval modal with 1-5 number hotkeys.
/// </summary>
public static class VibeScreen
{
    private static bool _altScreenActive;
    private static readonly List<string> InputHistory = new();

    // ── Alternate Screen Buffer ────────────────────────────────────────────
    public static void EnterAltScreen()
    {
        if (_altScreenActive) return;
        Console.Write("\x1b[?1049h\x1b[H"); // switch to alternate screen and home cursor
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

    // ── Header Bar ─────────────────────────────────────────────────────────
    public static void RenderHeader(AppConfig cfg)
    {
        var theme = Ui.Get(cfg);
        var accent = theme.AccentHex;
        var active = cfg.Active();
        var width = Math.Max(40, Console.WindowWidth);

        // Top line: ctv logo + subtitle
        var brand = $"[bold {accent}]ctv[/] [dim]· cool terminal vibe[/]";

        // Right side: provider/model pill + git branch
        var modelPill = active is not null
            ? $"[dim]{Markup.Escape(active.Name)}[/] · [bold {accent}]{Markup.Escape(active.Model ?? "-")}[/]"
            : "[yellow]no provider[/]";

        var branch = GetGitBranch();
        var branchText = !string.IsNullOrEmpty(branch) ? $" [dim] {Markup.Escape(branch)}[/]" : "";
        var cwdName = Path.GetFileName(Directory.GetCurrentDirectory());

        var leftText = brand;
        var rightText = $"{modelPill}{branchText} [dim]📁 {Markup.Escape(cwdName)}[/]";

        // Render split header
        var headerTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        headerTable.AddColumn(new TableColumn("Left").LeftAligned());
        headerTable.AddColumn(new TableColumn("Right").RightAligned());
        headerTable.AddRow(new Markup(leftText), new Markup(rightText));

        AnsiConsole.Write(headerTable);
        AnsiConsole.Write(new Rule().RuleStyle(Style.Parse(accent)));
    }

    // ── Message Rendering ──────────────────────────────────────────────────
    public static void RenderMessage(ChatMessage m, AppConfig cfg)
    {
        var theme = Ui.Get(cfg);
        var accent = theme.AccentHex;

        if (m.Role == "user")
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold cyan]❯ You[/]");
            var panel = new Panel(new Markup(Markup.Escape(m.Content)))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("cyan"),
                Padding = new Padding(1, 0, 1, 0)
            };
            AnsiConsole.Write(panel);
        }
        else if (m.Role == "assistant")
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold {accent}]✦ ctv[/]");
            RenderAssistantContent(m.Content, accent);
        }
    }

    public static void RenderAssistantContent(string text, string accentHex)
    {
        // Check if there are tool blocks or normal text
        var toolRegex = new Regex(@"(\[Tool Result:.*?\]|```.*?```|<bash>.*?</bash>|<write_file.*?>.*?</write_file>|<read_file.*?>|<edit_file.*?>.*?</edit_file>)", RegexOptions.Singleline);
        var parts = toolRegex.Split(text);

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;

            if (part.StartsWith("[Tool Result: bash") || part.StartsWith("<bash>"))
            {
                var content = CleanToolMarkup(part);
                var p = new Panel(new Markup($"[dim]{Markup.Escape(content)}[/]"))
                    .Header("[bold yellow]⚙ Shell Action[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(Style.Parse("yellow"));
                AnsiConsole.Write(p);
            }
            else if (part.StartsWith("[Tool Result: write_file") || part.StartsWith("<write_file"))
            {
                var content = CleanToolMarkup(part);
                var p = new Panel(new Markup($"[dim]{Markup.Escape(content)}[/]"))
                    .Header("[bold green]✏ File Write[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(Style.Parse("green"));
                AnsiConsole.Write(p);
            }
            else if (part.StartsWith("[Tool Result: read_file") || part.StartsWith("<read_file"))
            {
                var content = CleanToolMarkup(part);
                var p = new Panel(new Markup($"[dim]{Markup.Escape(content)}[/]"))
                    .Header("[bold blue]📖 File Read[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(Style.Parse("blue"));
                AnsiConsole.Write(p);
            }
            else if (part.StartsWith("```"))
            {
                // Code block
                var lines = part.Split('\n');
                var lang = lines.Length > 0 ? lines[0].Trim('`').Trim() : "";
                var code = lines.Length > 2 ? string.Join('\n', lines.Skip(1).Take(lines.Length - 2)) : part;
                var p = new Panel(new Markup(Markup.Escape(code)))
                    .Header(string.IsNullOrEmpty(lang) ? "[dim]code[/]" : $"[bold]{Markup.Escape(lang)}[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(Style.Parse("dim"));
                AnsiConsole.Write(p);
            }
            else
            {
                AnsiConsole.MarkupLine(Markup.Escape(part));
            }
        }
    }

    static string CleanToolMarkup(string s)
    {
        return s.Replace("<bash>", "").Replace("</bash>", "")
                .Replace("<read_file", "").Replace("/>", "")
                .Replace("<write_file", "").Replace("</write_file>", "").Trim();
    }

    // ── Full-Screen Composer (Prompt Box) ──────────────────────────────────
    public static string? ReadComposer(AppConfig cfg)
    {
        var theme = Ui.Get(cfg);
        var accent = theme.AccentHex;

        var buf = new StringBuilder();
        int cursor = 0;
        int histIdx = InputHistory.Count;
        string draft = "";

        var slashCommands = new[]
        {
            "/help", "/login", "/provider", "/model", "/theme", "/system",
            "/config", "/permissions", "/save", "/load", "/sessions", "/delete-session",
            "/clear", "/quit"
        };

        void RedrawPrompt()
        {
            var text = buf.ToString();
            var width = Math.Max(40, Console.WindowWidth);

            // Print composer box
            AnsiConsole.WriteLine();
            var hints = "[dim](@ attach file · ! shell command · / command)[/]";
            var promptHeader = $"[bold {accent}]Prompt[/] {hints}";

            var displayText = text.Length > 0 ? text : "[dim]Type a message or command...[/]";
            var renderedPrompt = new Panel(new Markup($"[bold {accent}]❯[/] [white]{Markup.Escape(displayText)}[/]"))
                .Header(promptHeader)
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse(accent));

            AnsiConsole.Write(renderedPrompt);

            // Footer bar
            var autoApprove = cfg.AutoApprove ? "[green]auto-approve: on[/]" : "[dim]auto-approve: off[/]";
            var footer = $"[dim]Enter Send · Shift+Enter Newline · Tab Complete · Ctrl+C Exit   |   {autoApprove}[/]";
            AnsiConsole.MarkupLine(footer);
        }

        RedrawPrompt();

        while (true)
        {
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
                    RedrawPrompt();
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
                return null; // exit / cancel
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
                    RedrawPrompt();
                }
                continue;
            }

            if (k.Key == ConsoleKey.Delete)
            {
                if (cursor < buf.Length)
                {
                    buf.Remove(cursor, 1);
                    RedrawPrompt();
                }
                continue;
            }

            if (k.Key == ConsoleKey.LeftArrow)
            {
                if (cursor > 0) cursor--;
                continue;
            }

            if (k.Key == ConsoleKey.RightArrow)
            {
                if (cursor < buf.Length) cursor++;
                continue;
            }

            if (k.Key == ConsoleKey.Home)
            {
                cursor = 0;
                continue;
            }

            if (k.Key == ConsoleKey.End)
            {
                cursor = buf.Length;
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
                    RedrawPrompt();
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
                    RedrawPrompt();
                }
                continue;
            }

            // Tab completion for commands and files
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
                        RedrawPrompt();
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
                            RedrawPrompt();
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
                RedrawPrompt();
                continue;
            }

            if (ctrl && k.Key == ConsoleKey.W)
            {
                // Delete word backward
                while (cursor > 0 && buf[cursor - 1] == ' ') { buf.Remove(cursor - 1, 1); cursor--; }
                while (cursor > 0 && buf[cursor - 1] != ' ') { buf.Remove(cursor - 1, 1); cursor--; }
                RedrawPrompt();
                continue;
            }

            if (!char.IsControl(k.KeyChar))
            {
                buf.Insert(cursor, k.KeyChar);
                cursor++;
                RedrawPrompt();
            }
        }
    }

    // ── Tool Approval Modal ────────────────────────────────────────────────
    public static PermissionOutcome ShowApprovalModal(string actionKey, string label, AppConfig cfg)
    {
        AnsiConsole.WriteLine();

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

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.D1 || key.KeyChar == '1')
            {
                AnsiConsole.MarkupLine("[green]✓ Allowed once.[/]");
                return PermissionOutcome.Allow;
            }
            if (key.Key == ConsoleKey.D2 || key.KeyChar == '2')
            {
                AnsiConsole.MarkupLine("[green]✓ Allowed for this session.[/]");
                return PermissionOutcome.Allow;
            }
            if (key.Key == ConsoleKey.D3 || key.KeyChar == '3')
            {
                if (!cfg.AllowedActions.Contains(actionKey, StringComparer.OrdinalIgnoreCase))
                {
                    cfg.AllowedActions.Add(actionKey);
                    ConfigStore.Save(cfg);
                }
                AnsiConsole.MarkupLine("[green]✓ Action saved to allow-list.[/]");
                return PermissionOutcome.Allow;
            }
            if (key.Key == ConsoleKey.D4 || key.KeyChar == '4')
            {
                cfg.AutoApprove = true;
                ConfigStore.Save(cfg);
                AnsiConsole.MarkupLine("[green]✓ Auto-approve enabled.[/]");
                return PermissionOutcome.Allow;
            }
            if (key.Key == ConsoleKey.D5 || key.KeyChar == '5' || key.Key == ConsoleKey.Escape || (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C))
            {
                AnsiConsole.MarkupLine("[red]✗ Denied.[/]");
                return PermissionOutcome.Deny;
            }
        }
    }
}
