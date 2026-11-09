using Spectre.Console;

namespace Ctv;

public static class Commands
{
    /// <summary>Every slash command (used for Tab completion and the footer hints).</summary>
    public static readonly string[] Names =
    {
        "/help", "/login", "/logout", "/signout", "/provider", "/model", "/theme", "/system", "/config", "/permissions",
        "/save", "/load", "/sessions", "/delete-session", "/questions", "/clear", "/quit",
        "/settings", "/vibe", "/easter", "/memories", "/extensions", "/auth", "/artifacts",
    };

    /// <summary>
    /// Handles a slash command. Returns false when the app should quit.
    /// Output printed here is captured into the transcript; prompts are native dialogs
    /// (they throw OperationCanceledException when the user presses Esc).
    /// </summary>
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
                ShowHelp();
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
                    if (name == cfg.Theme) break;
                    // Live preview: render banner with new theme before saving
                    var preview = cfg.Theme;
                    cfg.Theme = name;
                    VibeScreen.RedrawScreen(cfg);
                    AnsiConsole.MarkupLine($"Previewing theme [bold]{Markup.Escape(name)}[/] · Enter confirm · Esc cancel");
                    var confirm = Ui.Choose("Keep this theme?", new[] { "Yes", "No" });
                    if (confirm == "Yes")
                    {
                        ConfigStore.Save(cfg);
                        AnsiConsole.MarkupLine($"Theme set to [bold]{Markup.Escape(name)}[/]");
                    }
                    else
                    {
                        cfg.Theme = preview;
                        VibeScreen.RedrawScreen(cfg);
                        AnsiConsole.MarkupLine("[dim]Theme unchanged.[/]");
                    }
                    break;
                }

            case "/system":
                cfg.SystemPrompt = Ui.Ask("System prompt", cfg.SystemPrompt);
                ConfigStore.Save(cfg);
                AnsiConsole.MarkupLine("[dim]System prompt updated.[/]");
                break;

            case "/config":
                ShowConfig(cfg);
                break;

            case "/clear":
                history.Clear();
                SafetyGate.ClearSession();
                break;

            case "/settings":
            case "/prefs":
            case "/preferences":
                ShowSettings(cfg);
                break;

            case "/permissions":
                ShowPermissions(cfg);
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
                    Todos.Load(arg);
                    Questions.Load(arg);
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
                    foreach (var sessionName in list)
                    {
                        var loaded = Sessions.Load(sessionName);
                        st.AddRow(Markup.Escape(sessionName), loaded?.Count.ToString() ?? "0");
                    }
                    AnsiConsole.Write(st);
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

            case "/logout":
            case "/signout":
                {
                    if (Auth.SignOut(cfg))
                        AnsiConsole.MarkupLine("[dim]Signed out. API keys cleared.[/]");
                    else
                        AnsiConsole.MarkupLine("[dim]Nothing to sign out from.[/]");
                    break;
                }

            case "/memories":
                {
                    var mems = Memories.Load();
                    if (mems.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[dim]No persistent memories yet.[/]");
                        break;
                    }
                    var t = new Table().RoundedBorder().AddColumn("#").AddColumn("memory");
                    for (int i = 0; i < mems.Count; i++)
                        t.AddRow((i + 1).ToString(), Markup.Escape(mems[i]));
                    AnsiConsole.Write(t);
                    AnsiConsole.MarkupLine("[dim]Use /system to add a memory to the prompt, or edit ~/.config/ctv/memories.json[/]");
                    break;
                }

            case "/extensions":
                {
                    var cmds = Extensions.GetAllCommands();
                    if (cmds.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[dim]No extensions installed. Add JSON manifests to ~/.config/ctv/extensions/[/]");
                        break;
                    }
                    var t = new Table().RoundedBorder().AddColumn("command").AddColumn("description").AddColumn("shell command");
                    foreach (var c in cmds)
                        t.AddRow(Markup.Escape(c.Name), Markup.Escape(c.Description), Markup.Escape(c.ShellCommand));
                    AnsiConsole.Write(t);
                    break;
                }

            case "/auth":
                {
                    var statuses = Auth.StatusAll(cfg);
                    if (statuses.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[dim]No providers configured.[/]");
                        break;
                    }
                    var t = new Table().RoundedBorder().AddColumn("provider").AddColumn("kind").AddColumn("state").AddColumn("key source");
                    foreach (var s in statuses)
                    {
                        string state = s.State switch
                        {
                            AuthStateKind.Authenticated => "[green]authenticated[/]",
                            AuthStateKind.FromEnvironment => "[green]from env[/]",
                            _ => "[red]not configured[/]",
                        };
                        t.AddRow(Markup.Escape(s.ProviderName), s.Kind, state, Markup.Escape(s.KeySource ?? "-"));
                    }
                    AnsiConsole.Write(t);
                    break;
                }

            case "/artifacts":
                {
                    var blocks = CodeBlocks.Last;
                    if (blocks.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[dim]No code blocks in the last response.[/]");
                        break;
                    }

                    var artifactBlocks = blocks.Select(b => (b.Language, b.Code)).ToList();
                    var modal = new VibeScreen.ArtifactModal(artifactBlocks);
                    VibeScreen.RunModal(modal);

                    if (!modal.Cancelled && modal.Result.Length > 0)
                    {
                        // Copy result to clipboard is not available; show it in transcript instead
                        AnsiConsole.MarkupLine($"[green]Artifact {modal.SelectedIndex + 1} selected ({modal.Result.Length} chars).[/]");
                    }
                    break;
                }

            default:
                if (EasterEggs.TryHandle(line, out var egg) && egg is not null)
                {
                    AnsiConsole.Markup(egg);
                    if (!egg.EndsWith('\n')) AnsiConsole.WriteLine();
                    break;
                }
                AnsiConsole.MarkupLine($"[yellow]Unknown command[/] {Markup.Escape(cmd)} - try /help");
                break;
        }
        return true;
    }

    static void ShowHelp()
    {
        var t = new Table().RoundedBorder().AddColumn("command").AddColumn("what it does");
        t.AddRow("/login", "add or update a provider (OpenAI, Claude, Gemini, NVIDIA, Mistral, Ollama, custom...)");
        t.AddRow("/provider", "switch the active provider");
        t.AddRow("/model [[name]]", "browse the provider's models, or set one directly: /model name");
        t.AddRow("/theme", "pick a color theme");
        t.AddRow("/system", "edit the system prompt");
        t.AddRow("/config", "show config file and configured providers");
        t.AddRow("/settings", "settings & preferences menu (theme, step limit, auto-approve, provider, model...)");
        t.AddRow("/permissions", "view and manage the safety allow-list");
        t.AddRow("/save <name>", "save the current conversation");
        t.AddRow("/load <name>", "load a saved conversation");
        t.AddRow("/sessions", "list saved conversations");
        t.AddRow("/delete-session <name>", "remove a saved conversation");
        t.AddRow("/questions", "answer questions the AI asked earlier");
        t.AddRow("/artifacts", "list code blocks from the last assistant response");
        t.AddRow("/clear", "clear the conversation and session permissions");
        t.AddRow("/quit", "exit");
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine("[dim]@path  attach a file          !command  run a shell command (add two spaces + a question to ask about it)[/]");
        AnsiConsole.MarkupLine("[dim]Alt+Enter  new line · PgUp/PgDn  scroll · Tab  complete · Esc  cancel a running answer · Ctrl+C twice  quit[/]");
    }

    public static void AddProvider(AppConfig cfg)
    {
        const string custom = "Custom (OpenAI-compatible)";
        var choice = Ui.Choose("Choose a provider", Presets.All.Select(x => x.Name).Append(custom));
        var preset = Presets.All.FirstOrDefault(x => x.Name == choice);

        var pc = new ProviderConfig();
        if (preset is null)
        {
            pc.Id = Ui.Ask("Short id (e.g. mylab)");
            pc.Name = pc.Id;
            pc.Kind = "openai";
            pc.BaseUrl = Ui.Ask("Base URL (e.g. http://localhost:8000/v1)");
            pc.Model = Ui.Ask("Model");
        }
        else
        {
            pc.Id = preset.Id;
            pc.Name = preset.Name;
            pc.Kind = preset.Kind;
            pc.BaseUrl = preset.BaseUrl;
            pc.Model = Ui.Ask("Model", preset.Model);
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
                pc.ApiKey = Ui.Ask("API key (or env:VAR_NAME)", secret: true, allowEmpty: true);
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
            models = Ui.Busy("fetching models", () => Providers.ListModelsAsync(p).GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 200 ? ex.Message[..200] + "…" : ex.Message;
            AnsiConsole.MarkupLine($"[yellow]Couldn't list models:[/] {Markup.Escape(msg)}");
            models = new List<string>();
        }

        string chosen = models.Count == 0
            ? Ui.Ask("Model", p.Model)
            : Ui.Choose($"Model for {p.Name}", models);

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
        AnsiConsole.MarkupLine($"Active provider: [bold]{Markup.Escape(choice)}[/]");
    }

    static void ShowConfig(AppConfig cfg)
    {
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ConfigStore.FilePath)}[/]");
        var t = new Table().RoundedBorder()
            .AddColumn("").AddColumn("provider").AddColumn("kind").AddColumn("model").AddColumn("key");
        var active = cfg.Active();
        foreach (var p in cfg.Providers)
        {
            string key = p.ApiKey.StartsWith("env:", StringComparison.Ordinal) ? p.ApiKey
                : p.ApiKey.Length == 0 ? "none"
                : "set";
            t.AddRow(p == active ? "●" : "", Markup.Escape(p.Name), p.Kind, Markup.Escape(p.Model), Markup.Escape(key));
        }
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"theme: [bold]{Markup.Escape(cfg.Theme)}[/]");
    }

    // ── settings & preferences ──────────────────────────────────────────────────────────────────
    static string StepsText(int steps) => steps <= 0 ? "off (unlimited)" : steps.ToString();

    /// <summary>The /settings menu: stays open after every change so several things can be adjusted in one go.</summary>
    static void ShowSettings(AppConfig cfg)
    {
        if (!VibeScreen.Active)
        {
            AnsiConsole.MarkupLine("[yellow]The settings menu needs the full-screen UI.[/]");
            return;
        }

        int selected = 0;
        while (true)
        {
            var active = cfg.Active();
            var items = new List<VibeScreen.MenuItem>
            {
                new("Provider", active?.Name ?? "none", "The service your messages are sent to. Add more with /login."),
                new("Model", active?.Model ?? "-", "Browse the models your provider offers, or type a model name."),
                new("Theme", cfg.Theme, "Colors of the banner, borders and accents. You can preview a theme before keeping it."),
                new("Max agent steps", StepsText(cfg.MaxSteps), "How many tool rounds the agent may run for one message before it pauses and waits for you. The agent is told this number and its progress. Default: 8."),
                new("Auto-approve", cfg.AutoApprove ? "on" : "off", "When on, file edits, shell commands and git actions run without asking. Only turn it on if you trust the model and the folder."),
                new("Allow-list", $"{cfg.AllowedActions.Count} saved", "Actions you chose to always allow. Review or remove them here."),
                new("System prompt", AnsiText.Truncate(cfg.SystemPrompt.Replace('\n', ' '), 30), "Instructions that shape how the assistant behaves in every conversation."),
                new("Config file", Path.GetFileName(ConfigStore.FilePath), "Where these settings are stored: " + ConfigStore.FilePath),
                new("Reset preferences", "", "Restore the theme, step limit and system prompt to their defaults. Providers, keys and the allow-list are kept."),
            };

            int? pick = VibeScreen.Menu("Settings & preferences", items, selected);
            if (pick is null) return;
            selected = pick.Value;

            try
            {
                ApplySetting(items[selected].Label, cfg);
            }
            catch (OperationCanceledException)
            {
                // Esc inside a sub-dialog: just go back to the menu.
            }
        }
    }

    static void ApplySetting(string label, AppConfig cfg)
    {
        switch (label)
        {
            case "Provider":
                SwitchProvider(cfg);
                break;

            case "Model":
                {
                    var p = cfg.Active();
                    if (p is null) VibeScreen.Flash("Add a provider first (/login)");
                    else PickModel(p, cfg);
                    break;
                }

            case "Theme":
                {
                    var name = Ui.Choose("Theme", Ui.Themes.Select(x => x.Name));
                    if (name == cfg.Theme) break;

                    var previous = cfg.Theme;
                    cfg.Theme = name;
                    VibeScreen.RedrawScreen(cfg); // live preview: borders and accents change right away
                    bool keep = false;
                    try
                    {
                        keep = Ui.Choose($"Keep the {name} theme?", new[] { "Yes, keep it", "No, go back" })
                            .StartsWith("Yes", StringComparison.Ordinal);
                    }
                    finally
                    {
                        if (keep)
                        {
                            ConfigStore.Save(cfg);
                            VibeScreen.Flash($"Theme: {name}");
                        }
                        else
                        {
                            cfg.Theme = previous;
                            VibeScreen.RedrawScreen(cfg);
                        }
                    }
                    break;
                }

            case "Max agent steps":
                SetMaxSteps(cfg);
                break;

            case "Auto-approve":
                {
                    if (cfg.AutoApprove)
                    {
                        SafetyGate.DisableAutoApprove(cfg);
                        VibeScreen.Flash("Auto-approve is off");
                    }
                    else
                    {
                        var ok = Ui.Choose("Let the agent run commands and edit files without asking?",
                            new[] { "No, keep asking", "Yes, turn auto-approve on" });
                        if (ok.StartsWith("Yes", StringComparison.Ordinal))
                        {
                            cfg.AutoApprove = true;
                            ConfigStore.Save(cfg);
                            VibeScreen.Flash("Auto-approve is on");
                        }
                    }
                    break;
                }

            case "Allow-list":
                ManageAllowList(cfg);
                break;

            case "System prompt":
                cfg.SystemPrompt = Ui.Ask("System prompt", cfg.SystemPrompt);
                ConfigStore.Save(cfg);
                VibeScreen.Flash("System prompt updated");
                break;

            case "Config file":
                VibeScreen.Flash(ConfigStore.FilePath, 6000);
                break;

            case "Reset preferences":
                {
                    var sure = Ui.Choose("Reset theme, step limit and system prompt to their defaults?",
                        new[] { "No, keep my settings", "Yes, reset" });
                    if (sure.StartsWith("Yes", StringComparison.Ordinal))
                    {
                        var defaults = new AppConfig();
                        cfg.Theme = defaults.Theme;
                        cfg.MaxSteps = defaults.MaxSteps;
                        cfg.SystemPrompt = defaults.SystemPrompt;
                        ConfigStore.Save(cfg);
                        VibeScreen.RedrawScreen(cfg);
                        VibeScreen.Flash("Preferences reset to defaults");
                    }
                    break;
                }
        }
    }

    static void SetMaxSteps(AppConfig cfg)
    {
        const string off = "Off (no limit)";
        const string custom = "Custom number…";
        string current = cfg.MaxSteps <= 0 ? off : cfg.MaxSteps.ToString();
        var choice = Ui.Choose($"Max agent steps per message (now: {current})",
            new[] { "4", "8 (default)", "16", "32", "64", off, custom });

        int value;
        if (choice == off)
        {
            value = 0;
        }
        else if (choice == custom)
        {
            while (true)
            {
                var text = Ui.Ask("Max steps (1-1000, or 0 for no limit)", cfg.MaxSteps.ToString());
                if (int.TryParse(text, out var n) && n >= 0 && n <= 1000)
                {
                    value = n;
                    break;
                }
                VibeScreen.Flash("Enter a whole number from 0 to 1000");
            }
        }
        else
        {
            value = int.Parse(choice.Split(' ')[0]);
        }

        cfg.MaxSteps = value;
        ConfigStore.Save(cfg);
        VibeScreen.Flash(value == 0 ? "Step limit is off" : $"Step limit: {value} per message");
    }

    static void ManageAllowList(AppConfig cfg)
    {
        if (cfg.AllowedActions.Count == 0)
        {
            VibeScreen.Flash("The allow-list is empty");
            return;
        }

        const string clearAll = "Clear the whole allow-list";
        const string back = "Back";
        var options = cfg.AllowedActions.ToList();
        options.Add(clearAll);
        options.Add(back);

        var pick = Ui.Choose("Allow-list · choose an entry to remove", options);
        if (pick == back) return;
        if (pick == clearAll)
        {
            cfg.AllowedActions.Clear();
            ConfigStore.Save(cfg);
            VibeScreen.Flash("Allow-list cleared");
            return;
        }

        SafetyGate.RemoveAllowedAction(pick, cfg);
        VibeScreen.Flash($"Removed: {pick}");
    }

    static void ShowPermissions(AppConfig cfg)
    {
        AnsiConsole.MarkupLine($"[bold]Auto-approve:[/] {(cfg.AutoApprove ? "[green]on[/]" : "[dim]off[/]")}");

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

        const string toggleAuto = "Toggle auto-approve";
        const string removeEntry = "Remove an allow-list entry";
        const string clearAll = "Clear entire allow-list";
        const string done = "Done";

        var action = Ui.Choose("What would you like to do?", [toggleAuto, removeEntry, clearAll, done]);

        switch (action)
        {
            case toggleAuto:
                if (cfg.AutoApprove) SafetyGate.DisableAutoApprove(cfg);
                else { cfg.AutoApprove = true; ConfigStore.Save(cfg); }
                AnsiConsole.MarkupLine($"Auto-approve is now [bold]{(cfg.AutoApprove ? "on" : "off")}[/].");
                break;

            case removeEntry when cfg.AllowedActions.Count > 0:
                var pick = Ui.Choose("Remove which entry?", cfg.AllowedActions.ToList());
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
