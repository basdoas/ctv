
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

CancellationTokenSource? gen = null;

// Cleanly enter alternate screen buffer (like mistral-vibe)
AppDomain.CurrentDomain.ProcessExit += (_, _) => VibeScreen.ExitAltScreen();
VibeScreen.EnterAltScreen();

// Ctrl+C stops a running answer; when idle it exits normally.
Console.CancelKeyPress += (_, e) =>
{
    if (gen is { } g) { e.Cancel = true; g.Cancel(); }
};

if (cfg.Providers.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]No provider configured yet - let's add one.[/]");
    Commands.AddProvider(cfg);
}

// Render any previous messages restored from session
foreach (var m in history)
{
    VibeScreen.AddMessage(m, cfg);
}

while (true)
{
    var line = VibeScreen.ReadComposer(cfg);
    if (line is null) break;
    line = line.Trim();
    if (line.Length == 0) continue;

    if (line.StartsWith('/'))
    {
        if (line.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            history.Clear();
            SafetyGate.ClearSession();
            VibeScreen.ClearTranscript();
            VibeScreen.RedrawScreen(cfg);
            continue;
        }

        if (!Commands.Handle(line, cfg, history)) break;
        VibeScreen.RedrawScreen(cfg);
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
        continue;
    }

    var processed = FileAttachment.ProcessAttachments(line, cfg);
    if (processed is null) continue;

    await Send(processed);
}

VibeScreen.ExitAltScreen();
AnsiConsole.MarkupLine("[dim]bye 👋[/]");
return;

async Task Send(string text)
{
    var pc = cfg.Active();
    if (pc is null)
    {
        AnsiConsole.MarkupLine("[red]No active provider.[/] Use /login");
        return;
    }

    var theme = Ui.Get(cfg);
    var userMsg = new ChatMessage("user", text);
    history.Add(userMsg);
    VibeScreen.AddMessage(userMsg, cfg);
    VibeScreen.RedrawScreen(cfg, status: "thinking…");

    var systemPrompt = SystemPrompts.Build(cfg);

    const int maxSteps = 8;
    for (int step = 0; step < maxSteps; step++)
    {
        var answer = new StringBuilder();
        var sw = Stopwatch.StartNew();
        gen = new CancellationTokenSource();
        bool canceled = false;

        VibeScreen.RedrawScreen(cfg, status: step == 0 ? "✦ thinking…" : "✦ processing tools…");

        try
        {
            var provider = Providers.Create(pc);
            await using var e = provider.StreamAsync(history, systemPrompt, gen.Token).GetAsyncEnumerator(gen.Token);

            while (await e.MoveNextAsync())
            {
                answer.Append(e.Current);
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            VibeScreen.AddMessage(new ChatMessage("system", $"Error: {ex.Message}"), cfg);
            VibeScreen.RedrawScreen(cfg);
            break;
        }
        finally
        {
            gen.Dispose();
            gen = null;
        }

        if (canceled)
        {
            VibeScreen.AddMessage(new ChatMessage("system", "(interrupted by user)"), cfg);
            VibeScreen.RedrawScreen(cfg);
            break;
        }

        var fullAnswer = answer.ToString();
        if (fullAnswer.Length == 0)
        {
            if (step == 0) history.RemoveAt(history.Count - 1);
            break;
        }

        var assistantMsg = new ChatMessage("assistant", fullAnswer);
        history.Add(assistantMsg);
        VibeScreen.AddMessage(assistantMsg, cfg);
        VibeScreen.RedrawScreen(cfg);

        Sessions.Save("current", history);
        Todos.Save("current");

        CodeBlocks.Parse(fullAnswer);

        // ── Handle questions the AI wants to ask ──────────────────────────
        var newQuestions = QuestionTools.ParseQuestions(fullAnswer);
        foreach (var q in newQuestions)
        {
            Questions.Add(q);
        }
        Questions.Save("current");

        if (newQuestions.Count > 0)
        {
            VibeScreen.AddMessage(new ChatMessage("system", $"❓ {newQuestions.Count} question(s) awaiting your answer. Use /questions to answer."), cfg);
            VibeScreen.RedrawScreen(cfg);
            break;
        }

        if (AgentTools.HasToolCalls(fullAnswer))
        {
            VibeScreen.RedrawScreen(cfg, status: "⚙ running tools…");

            gen = new CancellationTokenSource();
            string? toolResults = null;
            try
            {
                toolResults = await AgentTools.ExecuteToolCallsAsync(fullAnswer, cfg, gen.Token);
            }
            catch (OperationCanceledException)
            {
                VibeScreen.AddMessage(new ChatMessage("system", "(tool execution interrupted)"), cfg);
                VibeScreen.RedrawScreen(cfg);
                break;
            }
            finally
            {
                gen.Dispose();
                gen = null;
            }

            if (!string.IsNullOrEmpty(toolResults))
            {
                var toolMsg = new ChatMessage("user", toolResults);
                history.Add(toolMsg);
                VibeScreen.AddMessage(toolMsg, cfg);
                VibeScreen.RedrawScreen(cfg);

                Sessions.Save("current", history);
                Todos.Save("current");
                Questions.Save("current");
                continue;
            }
        }

        break;
    }
}
