
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ctv;

public sealed class ProviderConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>openai (any OpenAI-compatible API) | anthropic | gemini</summary>
    public string Kind { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    /// <summary>Literal key, or "env:VAR_NAME" to read it from an environment variable.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class AppConfig
{
    public string Theme { get; set; } = "aurora";
    public string? ActiveProvider { get; set; }
    public string ActiveAgent { get; set; } = "ask";
    public string SystemPrompt { get; set; } =
        "You are ctv, a helpful and concise assistant living in the user's terminal.";
    /// <summary>
    /// Maximum tool rounds the agent may run for one message before it stops and waits for the user.
    /// 0 turns the limit off. The agent is told this value (system prompt and tool results).
    /// </summary>
    public int MaxSteps { get; set; } = 8;
    public List<ProviderConfig> Providers { get; set; } = new();

    // ── Safety ────────────────────────────────────────────────────────────────
    /// <summary>When true every tool/file/shell action runs without prompting.</summary>
    public bool AutoApprove { get; set; } = false;
    /// <summary>Persistent action allow-list (key strings like "shell:ls -la" or "file:read").</summary>
    public List<string> AllowedActions { get; set; } = new();
    /// <summary>MCP server configuration.</summary>
    public McpConfig Mcp { get; set; } = new();

    public ProviderConfig? Active() =>
        Providers.FirstOrDefault(p => p.Id == ActiveProvider) ?? Providers.FirstOrDefault();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<TodoItem>))]
[JsonSerializable(typeof(List<PendingQuestion>))]
[JsonSerializable(typeof(McpConfig))]
[JsonSerializable(typeof(List<McpServerConfig>))]
[JsonSerializable(typeof(ExtensionManifest))]
internal partial class ConfigJson : JsonSerializerContext;

public sealed record Preset(string Id, string Name, string Kind, string BaseUrl, string Model, string? KeyEnv);

public static class Presets
{
    // Model names are just defaults - change them any time with /model.
    public static readonly Preset[] All =
    {
        new("openai",     "OpenAI",            "openai",    "https://api.openai.com/v1",                "gpt-5",                         "OPENAI_API_KEY"),
        new("anthropic",  "Anthropic (Claude)","anthropic", "https://api.anthropic.com",                "claude-sonnet-5-5",             "ANTHROPIC_API_KEY"),
        new("gemini",     "Google Gemini",     "gemini",    "https://generativelanguage.googleapis.com","gemini-2.5-flash",              "GEMINI_API_KEY"),
        new("nvidia",     "NVIDIA NIM",        "openai",    "https://integrate.api.nvidia.com/v1",      "nvidia/nemotron-3-super-120b-a12b", "NVIDIA_API_KEY"),
        new("mistral",    "Mistral",           "openai",    "https://api.mistral.ai/v1",                "mistral-large-latest",          "MISTRAL_API_KEY"),
        new("openrouter", "OpenRouter",        "openai",    "https://openrouter.ai/api/v1",             "openrouter/auto",               "OPENROUTER_API_KEY"),
        new("groq",       "Groq",              "openai",    "https://api.groq.com/openai/v1",           "llama-3.3-70b-versatile",       "GROQ_API_KEY"),
        new("ollama",     "Ollama (local)",    "openai",    "http://localhost:11434/v1",                "llama3.2",                      null),
    };
}

public static class ConfigStore
{
    public static string Dir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "ctv");
        }
    }

    public static string FilePath => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), ConfigJson.Default.AppConfig) ?? new AppConfig();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"warning: could not parse {FilePath}: {ex.Message}");
        }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, ConfigJson.Default.AppConfig));
        Platform.SetOwnerReadWrite(FilePath);
    }

    public static string ResolveKey(ProviderConfig p) =>
        p.ApiKey.StartsWith("env:", StringComparison.Ordinal)
            ? Environment.GetEnvironmentVariable(p.ApiKey[4..]) ?? ""
            : p.ApiKey;
}
