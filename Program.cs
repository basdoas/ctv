
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

// --agent <name> CLI argument
var cliArgs = Environment.GetCommandLineArgs();
for (int i = 0; i < cliArgs.Length - 1; i++)
{
    if (cliArgs[i] == "--agent" && i + 1 < cliArgs.Length)
    {
        cfg.ActiveAgent = cliArgs[i + 1];
        ConfigStore.Save(cfg);
        break;
    }
}

var history = new List<ChatMessage>();
var current = Sessions.Load("current");
if (current is not null)
{
    foreach (var m in current) history.Add(m);
    Todos.Load("current");
    Questions.Load("current");
}

var editor = new LineEditor();
CancellationTokenSource? gen = null;
var ui = new FullScreenUi(cfg);
ui.Start();

// Ctrl+C stops a running answer; when idle it exits normally.
Console.CancelKeyPress += (_, e) =>
{
    if (gen is { } g) { e.Cancel = true; g.Cancel(); }
};

if (cfg.Providers.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]No provider configured yet - let's add one.[/]");
    Commands.AddProvider(cfg);
    ui.Refresh();
}

while (true)
{
    var theme = Ui.Get(cfg);
    var active = cfg.Active();

    var line = editor.ReadLine();
    if (line is null) break;
    line = line.Trim();
    if (line.Length == 0) continue;

    if (line.StartsWith('/'))
    {
        if (!Commands.Handle(line, cfg, history)) break;
        ui.Refresh();
        continue;
    }

    if (line.StartsWith('!'))
    {
        gen = new CancellationTokenSource();
        string? prompt;
        try
        {
            prompt = await ToolRunner.TryRunAsync(line, cfg, gen.Token);
        }
        finally
        {
            gen.Dispose();
            gen = null;
        }

        if (prompt is not null)
        {
            await Send(prompt);
        }
        ui.Refresh();
        continue;
    }

    var processed = FileAttachment.ProcessAttachments(line, cfg);
    if (processed is null) continue;

    ui.AddUser(processed);
    await Send(processed);
}

ui.Stop();
return;

async Task Send(string text)
{
    var pc = cfg.Active();
    if (pc is null) { ui.AddSystem("[red]No active provider.[/] Use /login"); ui.Refresh(); return; }

    var theme = Ui.Get(cfg);
    history.Add(new ChatMessage("user", text));
    var systemPrompt = SystemPrompts.Build(cfg);

    const int maxSteps = 8;
    for (int step = 0; step < maxSteps; step++)
    {
        var answer = new StringBuilder();
        var sw = Stopwatch.StartNew();
        gen = new CancellationTokenSource();
        bool canceled = false;
        try
        {
            var provider = Providers.Create(pc);
            await using var e = provider.StreamAsync(history, systemPrompt, gen.Token).GetAsyncEnumerator(gen.Token);

            bool has = false;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse(theme.AccentHex))
                .StartAsync(step == 0 ? "thinking…" : "processing tools…", async _ => { has = await e.MoveNextAsync(); });

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
            canceled = true;
            Console.WriteLine();
            AnsiConsole.MarkupLine("[dim](stopped)[/]");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            if (ex.Message.StartsWith("HTTP 4"))
                AnsiConsole.MarkupLine("[dim]tip: /model browses the models your provider offers, /login updates your API key[/]");
            break;
        }
        finally
        {
            gen.Dispose();
            gen = null;
        }

        if (canceled) break;

        var fullAnswer = answer.ToString();
        if (fullAnswer.Length == 0)
        {
            if (step == 0) history.RemoveAt(history.Count - 1);
            break;
        }

        history.Add(new ChatMessage("assistant", fullAnswer));
        Sessions.Save("current", history);
        Todos.Save("current");

        ui.AddAssistant(fullAnswer);

        CodeBlocks.Parse(fullAnswer);
        if (CodeBlocks.Last.Count > 0)
        {
            ui.AddSystem($"[dim]📦 {CodeBlocks.Last.Count} code block(s) detected. Use /expand <n> or /codeblocks[/]");
        }

        // ── Handle questions the AI wants to ask ──────────────────────────
        var newQuestions = QuestionTools.ParseQuestions(fullAnswer);
        foreach (var q in newQuestions)
        {
            Questions.Add(q);
        }
        Questions.Save("current");

        if (newQuestions.Count > 0)
        {
            ui.AddSystem($"[dim]❓ {newQuestions.Count} question(s) awaiting your answer. Use /questions to answer.[/]");
            // Don't continue the AI loop; wait for user to answer via /questions
            break;
        }

        if (AgentTools.HasToolCalls(fullAnswer))
        {
            gen = new CancellationTokenSource();
            string? toolResults = null;
            try
            {
                toolResults = await AgentTools.ExecuteToolCallsAsync(fullAnswer, cfg, gen.Token);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[dim](tool execution interrupted)[/]");
                break;
            }
            finally
            {
                gen.Dispose();
                gen = null;
            }

            if (!string.IsNullOrEmpty(toolResults))
            {
                history.Add(new ChatMessage("user", toolResults));
                Sessions.Save("current", history);
                Todos.Save("current");
                Questions.Save("current");
                ui.AddSystem(toolResults);
                ui.Refresh();
                continue;
            }
        }

        break;
    }
}
