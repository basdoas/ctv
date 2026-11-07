using System.Diagnostics;
using System.Text;
using Ctv;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

// Spectre's ANSI auto-detection is wrong in some terminals, which breaks the selection menus.
// Force ANSI on unless the user opts out with NO_COLOR.
if (Environment.GetEnvironmentVariable("NO_COLOR") is null)
{
    var colorterm = Environment.GetEnvironmentVariable("COLORTERM") ?? "";
    AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = AnsiSupport.Yes,
        Interactive = InteractionSupport.Yes,
        ColorSystem = colorterm.Contains("truecolor") || colorterm.Contains("24bit")
            ? ColorSystemSupport.TrueColor
            : ColorSystemSupport.EightBit,
    });
}

var cfg = ConfigStore.Load();
var history = new List<ChatMessage>();
var editor = new LineEditor();
CancellationTokenSource? gen = null;

// Ctrl+C stops a running answer; when idle it exits normally.
Console.CancelKeyPress += (_, e) =>
{
    if (gen is { } g) { e.Cancel = true; g.Cancel(); }
};

Ui.Banner(cfg);
if (cfg.Providers.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]No provider configured yet - let's add one.[/]");
    Commands.AddProvider(cfg);
}

while (true)
{
    var theme = Ui.Get(cfg);
    var active = cfg.Active();
    AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape(active?.Name ?? "no provider")} · {Markup.Escape(active?.Model ?? "-")}[/]");
    AnsiConsole.Markup($"[bold {theme.AccentHex}]❯[/] ");

    var line = editor.ReadLine();
    if (line is null) break;
    line = line.Trim();
    if (line.Length == 0) continue;

    if (line.StartsWith('/'))
    {
        if (!Commands.Handle(line, cfg, history)) break;
        continue;
    }

    await Send(line);
}

AnsiConsole.MarkupLine("[dim]bye 👋[/]");
return;

async Task Send(string text)
{
    var pc = cfg.Active();
    if (pc is null) { AnsiConsole.MarkupLine("[red]No active provider.[/] Use /login"); return; }

    var theme = Ui.Get(cfg);
    history.Add(new ChatMessage("user", text));
    AnsiConsole.MarkupLine($"\n[bold {theme.AccentHex}]ctv[/]");

    var answer = new StringBuilder();
    var sw = Stopwatch.StartNew();
    gen = new CancellationTokenSource();
    try
    {
        var provider = Providers.Create(pc);
        await using var e = provider.StreamAsync(history, cfg.SystemPrompt, gen.Token).GetAsyncEnumerator(gen.Token);

        bool has = false;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse(theme.AccentHex))
            .StartAsync("thinking…", async _ => { has = await e.MoveNextAsync(); });

        while (has)
        {
            Console.Write(e.Current);
            answer.Append(e.Current);
            has = await e.MoveNextAsync();
        }
        Console.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{sw.Elapsed.TotalSeconds:0.0}s[/]");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("[dim](stopped)[/]");
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
        if (ex.Message.StartsWith("HTTP 4"))
            AnsiConsole.MarkupLine("[dim]tip: /model browses the models your provider offers, /login updates your API key[/]");
    }
    finally
    {
        gen.Dispose();
        gen = null;
    }

    if (answer.Length > 0) history.Add(new ChatMessage("assistant", answer.ToString()));
    else history.RemoveAt(history.Count - 1); // nothing came back: drop the unanswered user turn
}
