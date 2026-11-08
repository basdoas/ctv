using Spectre.Console;

namespace Ctv;

/// <summary>Authentication state for a provider.</summary>
public enum AuthStateKind
{
    /// <summary>No API key configured at all.</summary>
    NotConfigured,
    /// <summary>API key is set and usable.</summary>
    Authenticated,
    /// <summary>Key read from environment variable.</summary>
    FromEnvironment,
}

/// <summary>Snapshot of a provider's auth status.</summary>
public sealed record AuthStatus(
    string ProviderId,
    string ProviderName,
    string Kind,
    AuthStateKind State,
    string? KeySource,   // "env:VAR", "file", or null
    bool CanUse,
    string? Message
);

/// <summary>
/// Authentication helpers: check auth state per provider, sign out (clear key),
/// and persist keys. Modeled after Mistral Vibe's AcpAuthController but kept
/// simple for ctv's single-file config model.
/// </summary>
public static class Auth
{
    /// <summary>Assess auth state for every configured provider.</summary>
    public static List<AuthStatus> StatusAll(AppConfig cfg)
    {
        var list = new List<AuthStatus>();
        foreach (var p in cfg.Providers)
        {
            list.Add(StatusFor(p));
        }
        return list;
    }

    public static AuthStatus StatusFor(ProviderConfig p)
    {
        var key = p.ApiKey;
        if (string.IsNullOrEmpty(key))
            return new AuthStatus(p.Id, p.Name, p.Kind, AuthStateKind.NotConfigured, null, false, "No API key configured");

        if (key.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var envName = key[4..];
            var value = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrEmpty(value))
                return new AuthStatus(p.Id, p.Name, p.Kind, AuthStateKind.NotConfigured, $"env:{envName}", false, $"Environment variable {envName} is not set");
            return new AuthStatus(p.Id, p.Name, p.Kind, AuthStateKind.FromEnvironment, $"env:{envName}", true, $"Key from ${envName}");
        }

        return new AuthStatus(p.Id, p.Name, p.Kind, AuthStateKind.Authenticated, "file", true, "Key stored in config");
    }

    /// <summary>Sign out: clear the API key for a provider (or all if id is null).</summary>
    public static bool SignOut(AppConfig cfg, string? providerId = null)
    {
        var removed = false;
        foreach (var p in cfg.Providers)
        {
            if (providerId is not null && p.Id != providerId) continue;
            if (!string.IsNullOrEmpty(p.ApiKey) && !p.ApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            {
                p.ApiKey = "";
                removed = true;
            }
        }
        if (removed) ConfigStore.Save(cfg);
        return removed;
    }

    /// <summary>Interactive login: pick a provider preset or custom, then enter key.</summary>
    public static void Login(AppConfig cfg)
    {
        const string custom = "Custom (OpenAI-compatible)";
        var choice = Ui.Choose("Choose a provider", Presets.All.Select(x => x.Name).Append(custom).Append("Sign out"));
        if (choice == "Sign out")
        {
            HandleSignOut(cfg);
            return;
        }
        if (choice == custom)
        {
            AddCustomProvider(cfg);
            return;
        }

        var preset = Presets.All.FirstOrDefault(x => x.Name == choice);
        if (preset is null) return;

        var pc = new ProviderConfig
        {
            Id = preset.Id,
            Name = preset.Name,
            Kind = preset.Kind,
            BaseUrl = preset.BaseUrl,
            Model = Ui.Ask("Model", preset.Model),
        };

        // Check env var first
        if (preset.KeyEnv is not null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(preset.KeyEnv)))
        {
            pc.ApiKey = "env:" + preset.KeyEnv;
            AnsiConsole.MarkupLine($"[green]Found ${preset.KeyEnv} in your environment - using it.[/]");
        }
        else
        {
            pc.ApiKey = Ui.Ask("API key (or env:VAR_NAME)", secret: true, allowEmpty: true);
        }

        Save(cfg, pc);
    }

    static void AddCustomProvider(AppConfig cfg)
    {
        var pc = new ProviderConfig
        {
            Id = Ui.Ask("Short id (e.g. mylab)"),
            Kind = "openai",
            BaseUrl = Ui.Ask("Base URL (e.g. http://localhost:8000/v1)"),
            Model = Ui.Ask("Model"),
        };
        pc.Name = pc.Id;
        pc.ApiKey = Ui.Ask("API key (or env:VAR_NAME)", secret: true, allowEmpty: true);

        Save(cfg, pc);
    }

    static void Save(AppConfig cfg, ProviderConfig pc)
    {
        cfg.Providers.RemoveAll(x => x.Id == pc.Id);
        cfg.Providers.Add(pc);
        cfg.ActiveProvider = pc.Id;
        ConfigStore.Save(cfg);
        AnsiConsole.MarkupLine($"[green]Saved.[/] Active provider: [bold]{Markup.Escape(pc.Name)}[/] / {Markup.Escape(pc.Model)}");
    }

    static void HandleSignOut(AppConfig cfg)
    {
        if (cfg.Providers.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No providers configured.[/]");
            return;
        }

        var choice = Ui.Choose("Sign out from which provider?",
            cfg.Providers.Select(p => p.Name).Append("All"));

        if (choice == "All")
        {
            SignOut(cfg);
            AnsiConsole.MarkupLine("[dim]All API keys cleared.[/]");
            return;
        }

        var provider = cfg.Providers.First(x => x.Name == choice);
        SignOut(cfg, provider.Id);
        AnsiConsole.MarkupLine($"[dim]Signed out from [bold]{Markup.Escape(provider.Name)}[/].[/]");
    }
}
