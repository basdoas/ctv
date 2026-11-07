
using System.Text.Json.Serialization;

namespace Ctv;

/// <summary>Configuration for a single MCP server connection.</summary>
public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

/// <summary>MCP configuration section in ctv config.</summary>
public sealed class McpConfig
{
    public List<McpServerConfig> Servers { get; set; } = new();
}
