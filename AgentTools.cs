
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Ctv;

public static class AgentTools
{
    static readonly Regex BashRegex = new(@"<bash>\s*(.*?)\s*</bash>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex ReadFileRegex = new(@"<read_file\s+path=[""'](?<path>[^""']+)[""']\s*/?>|<read_file>(?<path>.*?)</read_file>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex WriteFileRegex = new(@"<write_file\s+path=[""'](?<path>[^""']+)[""']\s*>(?<content>.*?)</write_file>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex EditFileRegex = new(@"<edit_file\s+path=[""'](?<path>[^""']+)[""']\s*>\s*<old>(?<old>.*?)</old>\s*<new>(?<new>.*?)</new>\s*</edit_file>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static bool HasToolCalls(string text) =>
        BashRegex.IsMatch(text) || ReadFileRegex.IsMatch(text) || WriteFileRegex.IsMatch(text) || EditFileRegex.IsMatch(text) || GitTools.HasGitCalls(text) || TodoTools.HasTodoCalls(text) || McpTools.HasMcpCalls(text);

    public static async Task<string?> ExecuteToolCallsAsync(string responseText, AppConfig cfg, CancellationToken ct)
    {
        if (!HasToolCalls(responseText)) return null;

        SafetyGate.ResetTurn();

        var sb = new StringBuilder();
        var results = new List<(string Type, string Detail, bool Ok)>();

        foreach (Match match in WriteFileRegex.Matches(responseText))
        {
            var rawPath = match.Groups["path"].Value.Trim();
            var content = match.Groups["content"].Value;
            var fullPath = PathResolver.Resolve(rawPath);

            var outcome = SafetyGate.RequestApproval(
                $"file:write:{fullPath}",
                $"Write file ({content.Length} chars): {fullPath}",
                cfg);

            if (outcome == PermissionOutcome.Deny)
            {
                results.Add(("write", rawPath, false));
                continue;
            }

            try
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await File.WriteAllTextAsync(fullPath, content, ct);
                AnsiConsole.MarkupLine($"[green]✓ Created/Updated file:[/] [bold]{Markup.Escape(fullPath)}[/]");
                results.Add(("write", fullPath, true));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error writing file:[/] {Markup.Escape(ex.Message)}");
                results.Add(("write", rawPath, false));
            }
        }

        foreach (Match match in EditFileRegex.Matches(responseText))
        {
            var rawPath = match.Groups["path"].Value.Trim();
            var oldText = match.Groups["old"].Value;
            var newText = match.Groups["new"].Value;
            var fullPath = PathResolver.Resolve(rawPath);

            var outcome = SafetyGate.RequestApproval(
                $"file:write:{fullPath}",
                $"Edit file: {fullPath}",
                cfg);

            if (outcome == PermissionOutcome.Deny)
            {
                results.Add(("edit", rawPath, false));
                continue;
            }

            try
            {
                if (!File.Exists(fullPath))
                {
                    results.Add(("edit", rawPath, false));
                    continue;
                }

                var content = await File.ReadAllTextAsync(fullPath, ct);
                int index = content.IndexOf(oldText);
                if (index < 0)
                {
                    results.Add(("edit", rawPath, false));
                    continue;
                }

                int count = 0;
                int pos = 0;
                while ((pos = content.IndexOf(oldText, pos)) >= 0)
                {
                    count++;
                    pos += oldText.Length;
                }
                if (count > 1)
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠ Found {count} occurrences; replacing the first one only.[/]");
                }

                var result = content.Remove(index, oldText.Length).Insert(index, newText);
                await File.WriteAllTextAsync(fullPath, result, ct);
                AnsiConsole.MarkupLine($"[green]✓ Edited file:[/] [bold]{Markup.Escape(fullPath)}[/]");
                results.Add(("edit", fullPath, true));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error editing file:[/] {Markup.Escape(ex.Message)}");
                results.Add(("edit", rawPath, false));
            }
        }

        foreach (Match match in ReadFileRegex.Matches(responseText))
        {
            var rawPath = match.Groups["path"].Value.Trim();
            var fullPath = PathResolver.Resolve(rawPath);

            var outcome = SafetyGate.RequestApproval(
                SafetyGate.FileReadKey,
                $"Read file: {fullPath}",
                cfg);

            if (outcome == PermissionOutcome.Deny)
            {
                results.Add(("read", rawPath, false));
                continue;
            }

            try
            {
                if (!File.Exists(fullPath))
                {
                    results.Add(("read", rawPath, false));
                    continue;
                }

                const int maxChars = 48 * 1024;
                var text = await File.ReadAllTextAsync(fullPath, ct);
                if (text.Length > maxChars) text = text[..maxChars] + "\n... [content truncated]";

                AnsiConsole.MarkupLine($"[dim]✓ Read {Markup.Escape(fullPath)} ({text.Length} chars)[/]");
                results.Add(("read", fullPath, true));

                sb.AppendLine($"[Tool Result: read_file path=\"{rawPath}\"]");
                sb.AppendLine("```");
                sb.AppendLine(text);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error reading file:[/] {Markup.Escape(ex.Message)}");
                results.Add(("read", rawPath, false));
            }
        }

        foreach (Match match in BashRegex.Matches(responseText))
        {
            var rawCmd = match.Groups[1].Value.Trim();

            var (isDangerous, dangerReason) = SafetyPolicies.CheckShellCommand(rawCmd);
            if (isDangerous)
            {
                SafetyPolicies.Audit("shell", $"BLOCKED dangerous command: {rawCmd}", false);
                results.Add(("bash", rawCmd, false));
                sb.AppendLine($"[Tool Result: bash command=\"{rawCmd}\"]");
                sb.AppendLine($"Blocked: {dangerReason}");
                sb.AppendLine();
                continue;
            }

            var (allowed, exitCode, output) = await ToolRunner.ExecuteCommandAsync(rawCmd, cfg, ct);

            if (allowed)
            {
                AnsiConsole.MarkupLine($"[green]✓ Shell:[/] [bold]{Markup.Escape(rawCmd)}[/] (exit {exitCode})");
                results.Add(("bash", rawCmd, true));
            }
            else
            {
                results.Add(("bash", rawCmd, false));
            }

            sb.AppendLine($"[Tool Result: bash command=\"{rawCmd}\"]");
            if (!allowed)
            {
                sb.AppendLine("Permission denied by user.");
            }
            else
            {
                sb.AppendLine($"Exit Code: {exitCode}");
                sb.AppendLine("Output:");
                sb.AppendLine("```");
                sb.AppendLine(string.IsNullOrWhiteSpace(output) ? "(no output)" : output.TrimEnd());
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }

        var gitResults = await GitTools.ExecuteGitCallsAsync(responseText, cfg, ct);
        if (!string.IsNullOrEmpty(gitResults))
        {
            sb.Append(gitResults);
        }

        var todoResults = TodoTools.ExecuteTodoCalls(responseText);
        if (!string.IsNullOrEmpty(todoResults))
        {
            sb.Append(todoResults);
        }

        var mcpResults = await McpTools.ExecuteMcpCallsAsync(responseText, cfg, ct);
        if (!string.IsNullOrEmpty(mcpResults))
        {
            sb.Append(mcpResults);
        }

        var okCount = results.Count(r => r.Ok);
        var failCount = results.Count(r => !r.Ok);
        if (okCount > 0 || failCount > 0)
        {
            var summary = new StringBuilder();
            summary.AppendLine("[Tool Result: summary]");
            if (okCount > 0)
                summary.AppendLine($"[green]✓ {okCount} action(s) completed[/]");
            if (failCount > 0)
                summary.AppendLine($"[red]✗ {failCount} action(s) failed or denied[/]");
            summary.AppendLine();

            var detailed = sb.ToString();
            sb.Clear();
            sb.Append(summary);
            sb.Append(detailed);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }
}
