
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Parses and executes MCP tool XML calls from AI responses:
///   <mcp_call server="name" tool="toolname"><arg key="value"/></mcp_call>
///
/// Returns formatted results sent back to the AI.
/// </summary>
public static class McpTools
{
    static readonly Regex McpCallRegex = new(
        @"<mcp_call\s+server=[""'](?<server>[^""']+)[""']\s+tool=[""'](?<tool>[^""']+)[""']\s*>(?<body>.*?)</mcp_call>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    static readonly Regex ArgRegex = new(
        @"<arg\s+key=[""'](?<key>[^""']+)[""']\s*>(?<val>.*?)</arg>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static bool HasMcpCalls(string text) => McpCallRegex.IsMatch(text);

    public static async Task<string?> ExecuteMcpCallsAsync(string responseText, AppConfig cfg, CancellationToken ct)
    {
        if (!HasMcpCalls(responseText)) return null;

        var sb = new StringBuilder();
        bool anyExecuted = false;

        foreach (Match match in McpCallRegex.Matches(responseText))
        {
            anyExecuted = true;
            var server = match.Groups["server"].Value.Trim();
            var tool = match.Groups["tool"].Value.Trim();
            var body = match.Groups["body"].Value;

            var args = new Dictionary<string, JsonElement>();
            foreach (Match am in ArgRegex.Matches(body))
            {
                var key = am.Groups["key"].Value.Trim();
                var val = am.Groups["val"].Value.Trim();
                args[key] = JsonDocument.Parse($"\"{val}\"").RootElement;
            }

            var fullName = $"mcp:{server}:{tool}";
            var actionKey = $"mcp:{fullName}";

            var outcome = SafetyGate.RequestApproval(
                actionKey,
                $"Call MCP tool: {fullName}",
                cfg);

            if (outcome == PermissionOutcome.Deny)
            {
                sb.AppendLine($"[Tool Result: mcp_call server=\"{server}\" tool=\"{tool}\"]");
                sb.AppendLine("Permission denied by user.");
                sb.AppendLine();
                continue;
            }

            try
            {
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    foreach (var (k, v) in args)
                    {
                        writer.WritePropertyName(k);
                        v.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                using var doc = JsonDocument.Parse(ms.ToArray());
                var result = await McpManager.CallToolAsync(fullName, doc.RootElement, ct);

                AnsiConsole.MarkupLine($"[green]✓ MCP tool:[/] [bold]{Markup.Escape(fullName)}[/]");

                sb.AppendLine($"[Tool Result: mcp_call server=\"{server}\" tool=\"{tool}\"]");
                sb.AppendLine("Success:");
                sb.AppendLine("```");
                sb.AppendLine(result);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]MCP error:[/] {Markup.Escape(ex.Message)}");
                sb.AppendLine($"[Tool Result: mcp_call server=\"{server}\" tool=\"{tool}\"]");
                sb.AppendLine($"Error: {ex.Message}");
                sb.AppendLine();
            }
        }

        return anyExecuted ? sb.ToString() : null;
    }
}
