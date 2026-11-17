
using System.Text.Json.Serialization;

namespace Ctv;

/// <summary>
/// Plugin specification compatible with both Claude Code's marketplace.json
/// and the Agent Skills specification (SKILL.md).
/// </summary>
public sealed class PluginSpec
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Category { get; set; } = "general";

    /// <summary>MCP servers this plugin adds.</summary>
    public List<McpServerConfig> McpServers { get; set; } = new();

    /// <summary>Skills this plugin provides.</summary>
    public List<PluginSkill> Skills { get; set; } = new();

    /// <summary>Shell commands this plugin adds.</summary>
    public List<ExtensionCommand> Commands { get; set; } = new();

    /// <summary>Installation instructions.</summary>
    public string Install { get; set; } = "";
}

/// <summary>A skill provided by a plugin.</summary>
public sealed class PluginSkill
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string File { get; set; } = "";  // path to SKILL.md or skill script
    public List<string> Capabilities { get; set; } = new();
}

/// <summary>Marketplace registry entry.</summary>
public sealed class MarketplaceEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";  // git URL or local path
    public string LocalPath { get; set; } = "";  // where it's cloned
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public bool Enabled { get; set; } = true;
}

/// <summary>Marketplace manifest (marketplace.json).</summary>
public sealed class MarketplaceManifest
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public List<PluginSpec> Plugins { get; set; } = new();
}
