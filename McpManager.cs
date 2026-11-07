
using System.Text.Json;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Manages MCP server connections for ctv.
/// Loads servers from config, keeps clients alive, and exposes tools.
/// </summary>
public static class McpManager
{
    static readonly Dictionary<string, McpClient> _clients = new();

    public static List<McpServerConfig> Servers(AppConfig cfg) =>
        cfg.Mcp.Servers.Where(s => s.Enabled).ToList();

    public static void StartAll(AppConfig cfg)
    {
        foreach (var server in Servers(cfg))
        {
            try
            {
                var client = new McpClient(server);
                _clients[server.Name] = client;
                AnsiConsole.MarkupLine($"[dim]MCP '{Markup.Escape(server.Name)}' connected ({client.Tools.Count} tools).[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]MCP '{Markup.Escape(server.Name)}' failed to start:[/] {Markup.Escape(ex.Message)}");
            }
        }
    }

    public static void StopAll()
    {
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
    }

    public static IReadOnlyDictionary<string, (McpClient Client, McpTool Tool)> AllTools()
    {
        var result = new Dictionary<string, (McpClient, McpTool)>();
        foreach (var (name, client) in _clients)
        {
            foreach (var (toolName, tool) in client.Tools)
                result[$"mcp:{name}:{toolName}"] = (client, tool);
        }
        return result;
    }

    public static async Task<string> CallToolAsync(string fullName, JsonElement arguments, CancellationToken ct)
    {
        // fullName format: mcp:<server>:<tool>
        var parts = fullName.Split(':', 3);
        if (parts.Length != 3 || !_clients.TryGetValue(parts[1], out var client))
            throw new InvalidOperationException($"Unknown MCP tool: {fullName}");
        return await client.CallToolAsync(parts[2], arguments, ct);
    }

    public static void Dispose()
    {
        StopAll();
    }
}
