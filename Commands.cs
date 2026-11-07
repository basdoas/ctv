
using Spectre.Console;

namespace Ctv;

public static class Commands
{
    /// <summary>Handles a slash command. Returns false when the app should quit.</summary>
    public static bool Handle(string line, AppConfig cfg, List<ChatMessage> history)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "/quit":
            case "/exit":
                return false;

            case "/help":
                var t = new Table().RoundedBorder().AddColumn("command").AddColumn("what it does");
                t.AddRow("/login", "add or update a provider (OpenAI, Claude, Gemini, NVIDIA, Mistral, Ollama, custom...)");
                t.AddRow("/provider", "switch the active provider");
                t.AddRow("/model [[name]]", "browse the provider's models, or set one directly: /model name");
                t.AddRow("/theme", "pick a color theme");
                t.AddRow("/system", "edit the system prompt");
                t.AddRow("/config", "show config file and configured providers");
                t.AddRow("/permissions", "view and manage the safety allow-list");
                t.AddRow("/save <name>", "save current conversation to a session");
                t.AddRow("/load <name>", "load a saved session (replaces current history)");
                t.AddRow("/sessions", "list saved sessions");
                t.AddRow("/delete-session <name>", "remove a saved session");
                t.AddRow("/clear", "clear the conversation and session permissions");
                t.AddRow("/quit", "exit (Ctrl+C stops a running answer)");
                AnsiConsole.WriteLine();
                AnsiConsole.Write(t);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim]File access:  prefix a path with [bold]@[/] → @./file.txt what does this do?[/]");
                AnsiConsole.MarkupLine("[dim]Shell access: prefix a command with [bold]![/] → !git log --oneline -10[/]");
                AnsiConsole.MarkupLine("[dim]              Add a question after two spaces → !ls -la  explain the output[/]");
                break;

            case "/login":
                AddProvider(cfg);
                break;

            case "/provider":
                SwitchProvider(cfg);
                break;

            case "/model":
                {
                    var p = cfg.Active();
                    if (p is null) { AnsiConsole.MarkupLine("[red]No provider yet.[/] Use /login"); break; }
                    if (arg.Length > 0)
                    {
                        p.Model = arg;
                        ConfigStore.Save(cfg);
                        AnsiConsole.MarkupLine($"Model set to [bold]{Markup.Escape(p.Model)}[/]");
                    }
                    else PickModel(p, cfg);
                    break;
                }

            case "/theme":
                {
                    var name = Ui.Choose("Theme", Ui.Themes.Select(x => x.Name));
                    cfg.Theme = name;
                    ConfigStore.Save(cfg);
                    AnsiConsole.Clear();
                    Ui.Banner(cfg);
                    break;
                }

            case "/system":
                cfg.SystemPrompt = AnsiConsole.Prompt(new TextPrompt<string>("System prompt").DefaultValue(cfg.SystemPrompt));
                ConfigStore.Save(cfg);
                break;

            case "/config":
                ShowConfig(cfg);
                break;

            case "/clear":
                history.Clear();
                SafetyGate.ClearSession();
                AnsiConsole.Clear();
                Ui.Banner(cfg);
                break;

            case "/save":
                {
                    if (string.IsNullOrWhiteSpace(arg))
                    {
                        AnsiConsole.MarkupLine("[red]Usage:[/] /save <name>");
                        break;
                    }
                    Sessions.Save(arg, history);
                    AnsiConsole.MarkupLine($"[green]Saved session '{Markup.Escape(arg)}' ({history.Count} messages).[/]");
                    break;
                }

            case "/load":
                {
                    if (string.IsNullOrWhiteSpace(arg))
                    {
                        AnsiConsole.MarkupLine("[red]Usage:[/] /load <name>");
                        break;
                    }
                    var loaded = Sessions.Load(arg);
                    if (loaded is null)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Session '{Markup.Escape(arg)}' not found.[/]");
                        break;
                    }
                    history.Clear();
                    foreach (var m in loaded) history.Add(m);
                    AnsiConsole.MarkupLine($"[green]Loaded session '{Markup.Escape(arg)}' ({history.Count} messages).[/]");
                    break;
                }

            case "/sessions":
                {
                    var list = Sessions.List();
                    if (list.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[dim]No saved sessions.[/]");
                        break;
                    }
                    var st = new Table().RoundedBorder().AddColumn("name").AddColumn("messages");
                    foreach (var name in list)
                    {
                        var loaded = Sessions.Load(name);
                        st.AddRow(Markup.Escape(name), loaded?.Count.ToString() ?? "0");
                    }
                    AnsiConsole.WriteLine();
                    AnsiConsole.Write(st);
                    AnsiConsole.WriteLine();
                    break;
                }

            case "/delete-session":
            case "/rm-session":
                {
                    if (string.IsNullOrWhiteSpace(arg))
                    {
                        AnsiConsole.MarkupLine("[red]Usage:[/] /delete-session <name>");
                        break;
                    }
                    Sessions.Delete(arg);
                    AnsiConsole.MarkupLine($"[dim]Deleted session '{Markup.Escape(arg)}'.[/]");
                    break;
                }

            default:
                AnsiConsole.MarkupLine($"[yellow]Unknown command[/] {Markup.Escape(cmd)} - try /help");
                break;
        }
        return true;
    }

    public static void AddProvider(AppConfig cfg)
    {
        const string custom = "Custom (OpenAI-compatible)";
        var choice = Ui.Choose("Choose a provider", Presets.All.Select(x => x.Name).Append(custom));
        var preset = Presets.All.FirstOrDefault(x => x.Name == choice);

        var pc = new ProviderConfig();
        if (preset is null)
        {
            pc.Id = AnsiConsole.Prompt(new TextPrompt<string>("Short id (e.g. mylab)"));
            pc.Name = pc.Id;
            pc.Kind = "openai";
            pc.BaseUrl = AnsiConsole.Prompt(new TextPrompt<string>("Base URL (e.g. http://localhost:8000/v1)"));
            pc.Model = AnsiConsole.Prompt(new TextPrompt<string>("Model"));
        }
        else
        {
            pc.Id = preset.Id;
            pc.Name = preset.Name;
            pc.Kind = preset.Kind;
            pc.BaseUrl = preset.BaseUrl;
            pc.Model = AnsiConsole.Prompt(new TextPrompt<string>("Model").DefaultValue(preset.Model));
        }

        if (preset is null || preset.KeyEnv is not null)
        {
            var env = preset?.KeyEnv;
            if (env is not null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(env)))
            {
                AnsiConsole.MarkupLine($"[green]Found ${env} in your environment - using it.[/]");
                pc.ApiKey = "env:" + env;
            }
            else
            {
                AnsiConsole.Markup("API key [dim](or env:VAR_NAME)[/]: ");
                pc.ApiKey = (new LineEditor().ReadLine(mask: '*') ?? "").Trim();
            }
        }

        cfg.Providers.RemoveAll(x => x.Id == pc.Id);
        cfg.Providers.Add(pc);
        cfg.ActiveProvider = pc.Id;
        ConfigStore.Save(cfg);
        AnsiConsole.MarkupLine($"[green]Saved.[/] Active provider: [bold]{Markup.Escape(pc.Name)}[/] / {Markup.Escape(pc.Model)}");
        AnsiConsole.MarkupLine("[dim]tip: /model lists the models your provider currently offers[/]");
    }

    static void PickModel(ProviderConfig p, AppConfig cfg)
    {
        List<string> models;
        try
        {
            models = AnsiConsole.Status().Start("fetching models…", _ => Providers.ListModelsAsync(p).GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 200 ? ex.Message[..200] + "…" : ex.Message;
            AnsiConsole.MarkupLine($"[yellow]Couldn't list models:[/] {Markup.Escape(msg)}");
            models = new List<string>();
        }

        string chosen;
        if (models.Count == 0)
        {
            chosen = AnsiConsole.Prompt(new TextPrompt<string>("Model").DefaultValue(p.Model));
        }
        else
        {
            try
            {
                chosen = AnsiConsole.Prompt(new SelectionPrompt<string>()
                    .Title($"Model for {Markup.Escape(p.Name)} [dim](type to search)[/]")
                    .PageSize(15)
                    .EnableSearch()
                    .AddChoices(models));
            }
            catch (NotSupportedException)
            {
                chosen = AnsiConsole.Prompt(new TextPrompt<string>("Model").DefaultValue(p.Model));
            }
        }

        p.Model = chosen;
        ConfigStore.Save(cfg);
        AnsiConsole.MarkupLine($"Model set to [bold]{Markup.Escape(p.Model)}[/]");
    }

    static void SwitchProvider(AppConfig cfg)
    {
        const string add = "+ add a provider";
        if (cfg.Providers.Count == 0) { AddProvider(cfg); return; }
        var choice = Ui.Choose("Active provider", cfg.Providers.Select(p => p.Name).Append(add));
        if (choice == add) { AddProvider(cfg); return; }
        cfg.ActiveProvider = cfg.Providers.First(p => p.Name == choice).Id;
        ConfigStore.Save(cfg);
    }

    static void ShowConfig(AppConfig cfg)
    {
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ConfigStore.FilePath)}[/]");
        var t = new Table().RoundedBorder()
            .AddColumn("").AddColumn("provider").AddColumn("kind").AddColumn("model").AddColumn("key");
        var active = cfg.Active();
        foreach (var p in cfg.Providers)
        {
            string key = p.ApiKey.StartsWith("env:") ? p.ApiKey
                : p.ApiKey.Length == 0 ? "none"
                : "set";
            t.AddRow(p == active ? "●" : "", Markup.Escape(p.Name), p.Kind, Markup.Escape(p.Model), Markup.Escape(key));
        }
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"theme: [bold]{Markup.Escape(cfg.Theme)}[/]");
    }

    static void ShowPermissions(AppConfig cfg)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Auto-approve:[/] {(cfg.AutoApprove ? "[green]on[/]" : "[dim]off[/]")}");
        AnsiConsole.WriteLine();

        if (cfg.AllowedActions.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No persistent allow-list entries.[/]");
        }
        else
        {
            var t = new Table().RoundedBorder()
                .AddColumn("#").AddColumn("allowed action");
            for (int i = 0; i < cfg.AllowedActions.Count; i++)
                t.AddRow((i + 1).ToString(), Markup.Escape(cfg.AllowedActions[i]));
            AnsiConsole.Write(t);
        }

        AnsiConsole.WriteLine();

        const string toggleAuto  = "Toggle auto-approve";
        const string removeEntry = "Remove an allow-list entry";
        const string clearAll    = "Clear entire allow-list";
        const string done        = "Done";

        var action = Ui.Choose("What would you like to do?",
            [toggleAuto, removeEntry, clearAll, done]);

        switch (action)
        {
            case toggleAuto:
                if (cfg.AutoApprove) SafetyGate.DisableAutoApprove(cfg);
                else { cfg.AutoApprove = true; ConfigStore.Save(cfg); }
                AnsiConsole.MarkupLine($"Auto-approve is now [bold]{(cfg.AutoApprove ? "on" : "off")}[/].");
                break;

            case removeEntry when cfg.AllowedActions.Count > 0:
                var pick = Ui.Choose("Remove which entry?", cfg.AllowedActions);
                SafetyGate.RemoveAllowedAction(pick, cfg);
                AnsiConsole.MarkupLine($"[dim]Removed: {Markup.Escape(pick)}[/]");
                break;

            case removeEntry:
                AnsiConsole.MarkupLine("[dim]Allow-list is already empty.[/]");
                break;

            case clearAll:
                cfg.AllowedActions.Clear();
                ConfigStore.Save(cfg);
                AnsiConsole.MarkupLine("[dim]Allow-list cleared.[/]");
                break;
        }
    }
}

