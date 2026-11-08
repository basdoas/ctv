using System.Diagnostics;
using System.Text;
using Ctv;

Console.OutputEncoding = Encoding.UTF8;

if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine("ctv needs an interactive terminal (stdin and stdout must not be redirected).");
    return 1;
}

var cfg = ConfigStore.Load();

// --agent <name> CLI argument
var cliArgs = Environment.GetCommandLineArgs();
for (int i = 0; i < cliArgs.Length - 1; i++)
{
    if (cliArgs[i] == "--agent")
    {
        cfg.ActiveAgent = cliArgs[i + 1];
        ConfigStore.Save(cfg);
        break;
    }
}

var history = new List<ChatMessage>();
try
{
    var current = Sessions.Load("current");
    if (current is not null)
    {
        foreach (var m in current) history.Add(m);
        Todos.Load("current");
        Questions.Load("current");
    }
}
catch (Exception)
{
    // A damaged saved session must not stop ctv from starting.
    history.Clear();
}

CancellationTokenSource? gen = null;
long lastCtrlC = 0;

AppDomain.CurrentDomain.ProcessExit += (_, _) => VibeScreen.ExitAltScreen();
VibeScreen.EnterAltScreen();
VibeScreen.RedrawScreen(cfg);

// Esc while busy cancels the running request.
VibeScreen.CancelAction = () =>
{
    try { gen?.Cancel(); } catch (ObjectDisposedException) { }
};

// Ctrl+C: stops a running request; while idle the prompt box asks for a second press to quit.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    var g = gen;
    if (g is null)
    {
        VibeScreen.NotifyCtrlC();
        return;
    }

    long now = Environment.TickCount64;
    if (now - lastCtrlC < 1500)
    {
        VibeScreen.ForceExit(); // pressed twice while something is stuck
        return;
    }
    lastCtrlC = now;
    try { g.Cancel(); } catch (ObjectDisposedException) { }
};

try
{
    VibeScreen.Capture(() => Ui.Banner(cfg));
    foreach (var m in history) VibeScreen.AddMessage(m, cfg);

    if (cfg.Providers.Count == 0)
    {
        VibeScreen.AddMessage(new ChatMessage("system", "No provider configured yet - let's add one."), cfg);
        RunCaptured(() => Commands.AddProvider(cfg));
    }

    while (true)
    {
        var line = VibeScreen.ReadComposer(cfg);
        if (line is null) break;
        if (line.Length == 0) continue;

        // ── slash commands ──────────────────────────────────────────────
        if (line.StartsWith('/'))
        {
            VibeScreen.AddCommandEcho(line);
            var cmd = line.Split(' ', 2)[0].ToLowerInvariant();

            if (cmd == "/clear")
            {
                history.Clear();
                SafetyGate.ClearSession();
                Sessions.Save("current", history);
                VibeScreen.ClearTranscript();
                VibeScreen.Capture(() => Ui.Banner(cfg));
                continue;
            }

            if (cmd == "/questions")
            {
                if (!Questions.Current.Any(q => !string.IsNullOrEmpty(q.Question)))
                {
                    VibeScreen.AddMessage(new ChatMessage("system", "No pending questions."), cfg);
                    continue;
                }

                var answers = QuestionCommands.Run();
                if (answers is not null) await Send(answers);
                continue;
            }

            bool keepGoing = true;
            RunCaptured(() => keepGoing = Commands.Handle(line, cfg, history));
            if (!keepGoing) break;

            if (cmd == "/load")
            {
                VibeScreen.Rebuild(history, cfg);
                VibeScreen.AddMessage(new ChatMessage("system", $"Conversation loaded ({history.Count} messages)."), cfg);
            }
            continue;
        }

        // ── !shell commands ─────────────────────────────────────────────
        if (line.StartsWith('!'))
        {
            VibeScreen.AddCommandEcho(line);
            var cts = new CancellationTokenSource();
            gen = cts;
            VibeScreen.SetBusy("running command");
            string? prompt = null;
            try
            {
                prompt = await VibeScreen.CaptureAsync<string?>(() => ToolRunner.TryRunAsync(line, cfg, cts.Token));
            }
            catch (OperationCanceledException)
            {
                VibeScreen.AddMessage(new ChatMessage("system", "(interrupted)"), cfg);
            }
            catch (Exception ex)
            {
                VibeScreen.AddMessage(new ChatMessage("system", $"Error: {ex.Message}"), cfg);
            }
            finally
            {
                gen = null;
                cts.Dispose();
                VibeScreen.SetBusy(null);
            }

            if (prompt is not null) await Send(prompt);
            continue;
        }

        // ── normal message (with @file attachments) ─────────────────────
        string? processed = line;
        RunCaptured(() => processed = FileAttachment.ProcessAttachments(line, cfg));
        if (processed is null) continue;

        await Send(processed);
    }
}
catch (Exception ex)
{
    VibeScreen.ExitAltScreen();
    Console.Error.WriteLine($"ctv crashed: {ex}");
    return 1;
}

VibeScreen.ExitAltScreen();
Console.WriteLine("bye 👋");
return 0;

// ── helpers ─────────────────────────────────────────────────────────────────

// Runs code that prints with Spectre/Console (or opens dialogs) and puts its output into the transcript.
void RunCaptured(Action action)
{
    try
    {
        VibeScreen.Capture(action);
    }
    catch (OperationCanceledException)
    {
        VibeScreen.AddMessage(new ChatMessage("system", "(cancelled)"), cfg);
    }
    catch (Exception ex)
    {
        VibeScreen.AddMessage(new ChatMessage("system", $"Error: {ex.Message}"), cfg);
    }
}

async Task Send(string text)
{
    var pc = cfg.Active();
    if (pc is null)
    {
        VibeScreen.AddMessage(new ChatMessage("system", "No active provider. Use /login"), cfg);
        return;
    }

    var userMsg = new ChatMessage("user", text);
    history.Add(userMsg);
    VibeScreen.AddMessage(userMsg, cfg);

    var systemPrompt = SystemPrompts.Build(cfg);

    const int maxSteps = 8;
    for (int step = 0; step < maxSteps; step++)
    {
        var answer = new StringBuilder();
        var cts = new CancellationTokenSource();
        gen = cts;
        bool canceled = false;
        bool failed = false;
        var live = VibeScreen.BeginAssistant(cfg);
        VibeScreen.SetBusy(step == 0 ? "thinking" : "working");

        try
        {
            var provider = Providers.Create(pc);
            await using var e = provider.StreamAsync(history, systemPrompt, cts.Token).GetAsyncEnumerator(cts.Token);

            bool first = true;
            var clock = Stopwatch.StartNew();
            long lastPaint = 0;
            while (await e.MoveNextAsync())
            {
                answer.Append(e.Current);
                if (first)
                {
                    VibeScreen.SetBusy("writing");
                    first = false;
                }
                if (clock.ElapsedMilliseconds - lastPaint >= 50)
                {
                    VibeScreen.UpdateAssistant(live, answer.ToString());
                    lastPaint = clock.ElapsedMilliseconds;
                }
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            failed = true;
            VibeScreen.AddMessage(new ChatMessage("system", $"Error: {ex.Message}"), cfg);
        }
        finally
        {
            gen = null;
            cts.Dispose();
            VibeScreen.SetBusy(null);
        }

        var fullAnswer = answer.ToString();

        if (failed)
        {
            VibeScreen.EndAssistant(live, keep: false);
            if (step == 0) history.RemoveAt(history.Count - 1);
            break;
        }

        if (canceled)
        {
            VibeScreen.UpdateAssistant(live, fullAnswer);
            VibeScreen.EndAssistant(live, keep: fullAnswer.Length > 0);
            if (fullAnswer.Length > 0) history.Add(new ChatMessage("assistant", fullAnswer));
            else if (step == 0) history.RemoveAt(history.Count - 1);
            VibeScreen.AddMessage(new ChatMessage("system", "(interrupted)"), cfg);
            Sessions.Save("current", history);
            break;
        }

        if (fullAnswer.Length == 0)
        {
            VibeScreen.EndAssistant(live, keep: false);
            if (step == 0) history.RemoveAt(history.Count - 1);
            VibeScreen.AddMessage(new ChatMessage("system", "The model returned an empty answer."), cfg);
            break;
        }

        VibeScreen.UpdateAssistant(live, fullAnswer);
        VibeScreen.EndAssistant(live, keep: true);
        history.Add(new ChatMessage("assistant", fullAnswer));

        Sessions.Save("current", history);
        Todos.Save("current");
        CodeBlocks.Parse(fullAnswer);

        // ── questions the AI wants answered ─────────────────────────────
        var newQuestions = QuestionTools.ParseQuestions(fullAnswer);
        foreach (var q in newQuestions) Questions.Add(q);
        Questions.Save("current");

        if (newQuestions.Count > 0)
        {
            var answers = QuestionCommands.Run();
            if (answers is null)
            {
                VibeScreen.AddMessage(
                    new ChatMessage("system", $"{newQuestions.Count} question(s) pending - use /questions to answer."), cfg);
                break;
            }

            var answerMsg = new ChatMessage("user", answers);
            history.Add(answerMsg);
            VibeScreen.AddMessage(answerMsg, cfg);
            Sessions.Save("current", history);
            continue;
        }

        // ── tool calls (files, shell, git, MCP) ─────────────────────────
        if (!AgentTools.HasToolCalls(fullAnswer)) break;

        var toolCts = new CancellationTokenSource();
        gen = toolCts;
        VibeScreen.SetBusy("running tools");
        string? toolResults = null;
        bool toolsFailed = false;
        try
        {
            toolResults = await VibeScreen.CaptureAsync<string?>(
                () => AgentTools.ExecuteToolCallsAsync(fullAnswer, cfg, toolCts.Token));
        }
        catch (OperationCanceledException)
        {
            toolsFailed = true;
            VibeScreen.AddMessage(new ChatMessage("system", "(tool execution interrupted)"), cfg);
        }
        catch (Exception ex)
        {
            toolsFailed = true;
            VibeScreen.AddMessage(new ChatMessage("system", $"Error: tool execution failed: {ex.Message}"), cfg);
        }
        finally
        {
            gen = null;
            toolCts.Dispose();
            VibeScreen.SetBusy(null);
        }

        if (toolsFailed || string.IsNullOrEmpty(toolResults)) break;

        var toolMsg = new ChatMessage("user", toolResults);
        history.Add(toolMsg);
        VibeScreen.AddMessage(toolMsg, cfg);

        Sessions.Save("current", history);
        Todos.Save("current");
        Questions.Save("current");

        if (step == maxSteps - 1)
            VibeScreen.AddMessage(new ChatMessage("system", $"Stopped after {maxSteps} steps. Send a message to continue."), cfg);
        // otherwise loop: the model reads the tool results and answers
    }
}
