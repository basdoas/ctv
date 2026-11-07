
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Ctv;

public static class AgentTools
{
    static readonly Regex BashRegex = new("\x3Cbash\x3E\\s*(.*?)\\s*\x3C/bash\x3E", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex ReadFileRegex = new("\x3Cread_file\\s+path=[\"'](?<path>[^\"']+)[\"']\\s*/?\x3E|\x3Cread_file\x3E(?<path>.*?)\x3C/read_file\x3E", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex WriteFileRegex = new("\x3Cwrite_file\\s+path=[\"'](?<path>[^\"']+)[\"']\\s*\x3E(?<content>.*?)\x3C/write_file\x3E", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex EditFileRegex = new("\x3Cedit_file\\s+path=[\"'](?<path>[^\"']+)[\"']\\s*\x3E\\s*\x3Cold\x3E(?<old>.*?)\x3C/old\x3E\\s*\x3Cnew\x3E(?<new>.*?)\x3C/new\x3E\\s*\x3C/edit_file\x3E", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static bool HasToolCalls(string text) =>
        BashRegex.IsMatch(text) || ReadFileRegex.IsMatch(text) || WriteFileRegex.IsMatch(text) || EditFileRegex.IsMatch(text);

    public static async Task<string?> ExecuteToolCallsAsync(string responseText, AppConfig cfg, CancellationToken ct)
    {
        if (!HasToolCalls(responseText)) return null;

        var sb = new StringBuilder();
        bool anyExecuted = false;

        foreach (Match match in WriteFileRegex.Matches(responseText))
        {
            anyExecuted = true;
            var rawPath = match.Groups["path"].Value.Trim();
            var content = match.Groups["content"].Value;
            var fullPath = PathResolver.Resolve(rawPath);

            var outcome = SafetyGate.RequestApproval(
                $"file:write:{fullPath}",
                $"Write file ({content.Length} chars): {fullPath}",
                cfg);

            if (outcome == PermissionOutcome.Deny)
            {
                sb.AppendLine($"[Tool Result: write_file path=\"{rawPath}\"]");
                sb.AppendLine("Permission denied by user.");
                sb.AppendLine();
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

                sb.AppendLine($"[Tool Result: write_file path=\"{rawPath}\"]");
                sb.AppendLine($"Success. File written to: {fullPath} ({content.Length} bytes)");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error writing file:[/] {Markup.Escape(ex.Message)}");
                sb.AppendLine($"[Tool Result: write_file path=\"{rawPath}\"]");
                sb.AppendLine($"Error: {ex.Message}");
                sb.AppendLine();
            }
        }

        foreach (Match match in EditFileRegex.Matches(responseText))
        {
            anyExecuted = true;
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
                sb.AppendLine($"[Tool Result: edit_file path=\"{rawPath}\"]");
                sb.AppendLine("Permission denied by user.");
                sb.AppendLine();
                continue;
            }

            try
            {
                if (!File.Exists(fullPath))
                {
                    sb.AppendLine($"[Tool Result: edit_file path=\"{rawPath}\"]");
                    sb.AppendLine($"File not found: {fullPath}");
                    sb.AppendLine();
                    continue;
                }

                var content = await File.ReadAllTextAsync(fullPath, ct);
                int index = content.IndexOf(oldText);
                if (index < 0)
                {
                    sb.AppendLine($"[Tool Result: edit_file path=\"{rawPath}\"]");
                    sb.AppendLine("Error: search string not found in file.");
                    sb.AppendLine();
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

                sb.AppendLine($"[Tool Result: edit_file path=\"{rawPath}\"]");
                sb.AppendLine($"Success. Replaced {oldText.Length} chars with {newText.Length} chars in: {fullPath}");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error editing file:[/] {Markup.Escape(ex.Message)}");
                sb.AppendLine($"[Tool Result: edit_file path=\"{rawPath}\"]");
                sb.AppendLine($"Error: {ex.Message}");
                sb.AppendLine();
            }
        }

        foreach (Match match in ReadFileRegex.Matches(responseText))
        {
            anyExecuted = true;
            var rawPath = match.Groups["path"].Value.Trim();
            var fullPath = PathResolver.Resolve(rawPath);

            var outcome = SafetyGate.RequestApproval(
                SafetyGate.FileReadKey,
                $"Read file: {fullPath}",
                cfg);

            if (outcome == PermissionOutcome.Deny)
            {
                sb.AppendLine($"[Tool Result: read_file path=\"{rawPath}\"]");
                sb.AppendLine("Permission denied by user.");
                sb.AppendLine();
                continue;
            }

            try
            {
                if (!File.Exists(fullPath))
                {
                    sb.AppendLine($"[Tool Result: read_file path=\"{rawPath}\"]");
                    sb.AppendLine($"File not found: {fullPath}");
                    sb.AppendLine();
                    continue;
                }

                const int maxChars = 48 * 1024;
                var text = await File.ReadAllTextAsync(fullPath, ct);
                if (text.Length > maxChars) text = text[..maxChars] + "\n... [content truncated]";

                AnsiConsole.MarkupLine($"[dim]✓ Read {Markup.Escape(fullPath)} ({text.Length} chars)[/]");
                sb.AppendLine($"[Tool Result: read_file path=\"{rawPath}\"]");
                sb.AppendLine("```");
                sb.AppendLine(text);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error reading file:[/] {Markup.Escape(ex.Message)}");
                sb.AppendLine($"[Tool Result: read_file path=\"{rawPath}\"]");
                sb.AppendLine($"Error: {ex.Message}");
                sb.AppendLine();
            }
        }

        foreach (Match match in BashRegex.Matches(responseText))
        {
            anyExecuted = true;
            var rawCmd = match.Groups[1].Value.Trim();

            var (allowed, exitCode, output) = await ToolRunner.ExecuteCommandAsync(rawCmd, cfg, ct);

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

        return anyExecuted ? sb.ToString() : null;
    }
}
