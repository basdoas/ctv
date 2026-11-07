
using System.Collections.Concurrent;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Full-screen TUI for ctv — proper chat interface.
/// Renders a header (provider/model/agent), a scrollable transcript with
/// timestamped message bubbles, a status bar, and an input line.
/// </summary>
public sealed class FullScreenUi : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly List<ChatBubble> _bubbles = new();
    private string _pendingInput = "";
    private int _inputCursor;
    private bool _disposed;

    public FullScreenUi(AppConfig cfg)
    {
        _cfg = cfg;
    }

    public void Start()
    {
        AnsiConsole.Console.Write(ClearScreen);
        Render();
    }

    public void Refresh()
    {
        Render();
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        AnsiConsole.Console.Write(ClearScreen);
        AnsiConsole.MarkupLine("[dim]bye 👋[/]");
    }

    public void AddUser(string text)
    {
        _bubbles.Add(new ChatBubble("user", text, DateTime.Now));
        Render();
    }

    public void AddAssistant(string text)
    {
        if (_bubbles.Count > 0 && _bubbles[^1].Role == "assistant" && _bubbles[^1].IsStreaming)
        {
            _bubbles[^1] = _bubbles[^1] with { Text = text, IsStreaming = false };
        }
        else
        {
            _bubbles.Add(new ChatBubble("assistant", text, DateTime.Now));
        }
        Render();
    }

    public void UpdateLastAssistant(string text)
    {
        if (_bubbles.Count > 0 && _bubbles[^1].Role == "assistant")
        {
            _bubbles[^1] = _bubbles[^1] with { Text = text, IsStreaming = true };
        }
        else
        {
            _bubbles.Add(new ChatBubble("assistant", text, DateTime.Now) { IsStreaming = true });
        }
        Render();
    }

    public void FinalizeAssistant()
    {
        if (_bubbles.Count > 0 && _bubbles[^1].Role == "assistant")
        {
            _bubbles[^1] = _bubbles[^1] with { IsStreaming = false };
            Render();
        }
    }

    public void AddSystem(string text)
    {
        _bubbles.Add(new ChatBubble("system", text, DateTime.Now));
        Render();
    }

    public void ClearHistory()
    {
        _bubbles.Clear();
        Render();
    }

    public void SetInput(string text)
    {
        _pendingInput = text;
        _inputCursor = text.Length;
        Render();
    }

    public void AppendInput(char c)
    {
        _pendingInput = _pendingInput.Insert(_inputCursor++, c.ToString());
        Render();
    }

    public void BackspaceInput()
    {
        if (_inputCursor > 0)
        {
            _pendingInput = _pendingInput.Remove(_inputCursor - 1, 1);
            _inputCursor--;
        }
        Render();
    }

    public string TakeInput()
    {
        var text = _pendingInput;
        _pendingInput = "";
        _inputCursor = 0;
        Render();
        return text;
    }

    public void SetTheme(string themeName)
    {
        Render();
    }

    private void Render()
    {
        AnsiConsole.Console.Write(ClearScreen);
        AnsiConsole.Write(GeneratePanel());
    }

    private Panel GeneratePanel()
    {
        var theme = Ui.Get(_cfg);
        var accent = theme.AccentHex;
        var active = _cfg.Active();

        // ── Header ──────────────────────────────────────────────────────
        var providerLine = active is not null
            ? $"[bold {accent}]ctv[/]  [dim]{Markup.Escape(active.Name)}[/] · [bold]{Markup.Escape(active.Model ?? "-")}[/] · agent: [bold]{Markup.Escape(_cfg.ActiveAgent)}[/]"
            : $"[bold {accent}]ctv[/]  [dim]no provider — use /login[/]";

        var header = new Panel(
            new Markup(providerLine))
            .Header($"[bold {accent}]cool terminal vibe client[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse(accent));

        // ── Transcript ──────────────────────────────────────────────────
        var transcriptContent = _bubbles.Count > 0
            ? string.Join("\n\n", _bubbles.Select(b => b.Render(accent)))
            : "[dim]No conversation yet. Type a message or use /help.[/]";

        var transcript = new Panel(new Markup(transcriptContent))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 0, 1, 0),
        };

        // ── Status bar ──────────────────────────────────────────────────
        var statusParts = new List<string>();
        statusParts.Add(_cfg.AutoApprove ? "[green]auto-approve[/]" : "[dim]manual approve[/]");
        statusParts.Add($"[{accent}]/{_cfg.AllowedActions.Count} allowed[/]");
        var status = new Panel(new Markup(string.Join("   ", statusParts)))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 0, 1, 0),
        };

        // ── Input ───────────────────────────────────────────────────────
        var inputText = _pendingInput.Length > 0 ? Markup.Escape(_pendingInput) : "";
        var cursor = _inputCursor < _pendingInput.Length ? "[bold white]█[/]" : "";
        var before = Markup.Escape(_pendingInput[.._inputCursor]);
        var after = _inputCursor < _pendingInput.Length ? Markup.Escape(_pendingInput[_inputCursor..]) : "";
        var inputLine = $"[bold {accent}]❯[/] [white]{before}{cursor}{after}[/]";
        var input = new Panel(new Markup(inputLine))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 0, 1, 0),
        };

        var layout = new Layout()
            .SplitRows(
                new Layout(header).Size(3),
                new Layout(transcript),
                new Layout(status).Size(1),
                new Layout(input).Size(3)
            );

        return new Panel(layout)
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse(accent));
    }

    public void Dispose() => Stop();

    private static readonly string ClearScreen = "\x1b[2J\x1b[H";
}

internal sealed record ChatBubble
{
    public string Role { get; init; }
    public string Text { get; init; }
    public DateTime Time { get; init; }
    public bool IsStreaming { get; init; }

    public ChatBubble(string role, string text, DateTime time)
    {
        Role = role;
        Text = text;
        Time = time;
    }

    public string Render(string accentHex)
    {
        var (icon, color, label) = Role switch
        {
            "user" => ("👤", "cyan", "You"),
            "assistant" => ("🤖", accentHex, IsStreaming ? "ctv…" : "ctv"),
            _ => ("ℹ️", "gray", "System"),
        };

        var time = Time.ToString("HH:mm");
        var streaming = IsStreaming ? " [dim]…[/]" : "";
        return $$"""
        [bold {{color}}]{{icon}} {{label}}[/] [dim]{{time}}{{streaming}}[/]
        {{Text}}
        """;
    }
}
