
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Minimal stdio-based MCP (Model Context Protocol) client.
/// Spawns an MCP server process, speaks JSON-RPC 2.0 over stdin/stdout,
/// discovers tools, and forwards tool calls.
/// </summary>
public sealed class McpClient : IDisposable
{
    readonly Process _proc;
    readonly StreamWriter _stdin;
    readonly StreamReader _stdout;
    int _nextId = 1;
    readonly Dictionary<string, McpTool> _tools = new();

    public IReadOnlyDictionary<string, McpTool> Tools => _tools;

    public McpClient(McpServerConfig server)
    {
        var psi = new ProcessStartInfo
        {
            FileName = server.Command,
            Arguments = string.Join(" ", server.Args.Select(a => $"\"{a}\"")),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (k, v) in server.Env)
            psi.EnvironmentVariables[k] = v;

        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start MCP server: {server.Name}");
        _stdin = _proc.StandardInput;
        _stdout = _proc.StandardOutput;

        // Initialize MCP session
        var init = SendRequest("initialize", "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"roots\":{\"listChanged\":true}},\"clientInfo\":{\"name\":\"ctv\",\"version\":\"1.0.0\"}}");
        SendNotification("initialized");

        var listTools = SendRequest("tools/list", "{}");
        if (listTools.TryGetProperty("tools", out var toolsEl))
        {
            foreach (var t in toolsEl.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString() ?? "";
                var desc = t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var inputSchema = t.TryGetProperty("inputSchema", out var s) ? s : default(JsonElement?);
                _tools[name] = new McpTool(name, desc, inputSchema);
            }
        }
    }

    /// <summary>Send a JSON-RPC request and wait for the response.</summary>
    JsonElement SendRequest(string method, string? rawParamsJson = null)
    {
        var id = _nextId++;
        var line = rawParamsJson is not null
            ? $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{rawParamsJson}}}"
            : $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\"}}";
        _stdin.WriteLine(line);
        _stdin.Flush();

        while (true)
        {
            var respLine = _stdout.ReadLine();
            if (respLine is null) throw new InvalidOperationException("MCP server closed stdout.");
            var doc = JsonDocument.Parse(respLine);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var rid) && rid.GetInt32() == id)
                return root.GetProperty("result");
        }
    }

    void SendNotification(string method, string? rawParamsJson = null)
    {
        var line = rawParamsJson is not null
            ? $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{rawParamsJson}}}"
            : $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\"}}";
        _stdin.WriteLine(line);
        _stdin.Flush();
    }

    /// <summary>Call an MCP tool and return the result as a string.</summary>
    public async Task<string> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        var id = _nextId++;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteNumber("id", id);
            w.WriteString("method", "tools/call");
            w.WriteStartObject("params");
            w.WriteString("name", name);
            w.WritePropertyName("arguments");
            arguments.WriteTo(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        var line = Encoding.UTF8.GetString(ms.ToArray());
        _stdin.WriteLine(line);
        _stdin.Flush();

        while (true)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException();
            var respLine = await _stdout.ReadLineAsync(ct);
            if (respLine is null) throw new InvalidOperationException("MCP server closed stdout.");
            var doc = JsonDocument.Parse(respLine);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var rid) && rid.GetInt32() == _nextId - 1)
            {
                if (root.TryGetProperty("error", out var err))
                    throw new InvalidOperationException($"MCP error: {err}");
                var result = root.GetProperty("result");
                return result.ToString() ?? "";
            }
        }
    }

    public void Dispose()
    {
        try { _stdin?.Dispose(); } catch { }
        try { _stdout?.Dispose(); } catch { }
        try { if (!_proc.HasExited) _proc.Kill(); } catch { }
        _proc.Dispose();
    }
}

/// <summary>Discovered MCP tool.</summary>
public sealed record McpTool(string Name, string Description, JsonElement? InputSchema);
