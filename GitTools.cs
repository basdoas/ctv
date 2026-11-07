
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Handles Git XML tool calls from AI responses:
/// <c><git_status /></c>, <c><git_diff path="file.cs" /></c>, <c><git_log count="10" /></c>,
/// <c><git_add>.</git_add></c>, <c><git_commit>message</git_commit></c>,
/// <c><git_branch /></c>, <c><git_push /></c>, <c><git_pull /></c>
///
/// Each command is gated through <see cref="SafetyGate"/> with keys like <c>git:status</c>,
/// <c>git:commit</c>, etc., so users can approve Git actions independently of shell access.
/// </summary>
public static class GitTools
{
    static readonly Regex GitStatusRegex = new(@"<git_status\s*/>|<git_status>(?<arg>.*?)</git_status>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex GitDiffRegex   = new(@"<git_diff(?:\s+path=[""'](?<path>[^""']+)[""'])?\s*/?>|<git_diff>(?<arg>.*?)</git_diff>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex GitLogRegex    = new(@"<git_log\s+count=[""'](?<count>\d+)[""']\s*/?>|<git_log>(?<arg>.*?)</git_log>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex GitAddRegex    = new(@"<git_add>(?<arg>.*?)</git_add>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex GitCommitRegex = new(@"<git_commit>(?<arg>.*?)</git_commit>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex GitBranchRegex = new(@"<git_branch\s*/>|<git_branch>(?<arg>.*?)</git_branch>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex GitPushRegex   = new(@"<git_push\s*/>|<git_push>(?<arg>.*?)</git_push>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex GitPullRegex   = new(@"<git_pull\s*/>|<git_pull>(?<arg>.*?)</git_pull>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static bool HasGitCalls(string text) =>
        GitStatusRegex.IsMatch(text) || GitDiffRegex.IsMatch(text) || GitLogRegex.IsMatch(text) ||
        GitAddRegex.IsMatch(text) || GitCommitRegex.IsMatch(text) || GitBranchRegex.IsMatch(text) ||
        GitPushRegex.IsMatch(text) || GitPullRegex.IsMatch(text);

    public static async Task<string?> ExecuteGitCallsAsync(string responseText, AppConfig cfg, CancellationToken ct)
    {
        if (!HasGitCalls(responseText)) return null;

        var sb = new StringBuilder();
        bool anyExecuted = false;

        foreach (Match match in GitStatusRegex.Matches(responseText))
        {
            anyExecuted = true;
            var arg = match.Groups["arg"].Value.Trim();
            var cmd = arg.Length > 0 ? $"status {arg}" : "status";
            await RunGit(sb, "git_status", cmd, cfg, ct);
        }

        foreach (Match match in GitDiffRegex.Matches(responseText))
        {
            anyExecuted = true;
            var path = match.Groups["path"].Value.Trim();
            var arg = match.Groups["arg"].Value.Trim();
            var cmd = path.Length > 0 ? $"diff {path}" : (arg.Length > 0 ? $"diff {arg}" : "diff");
            await RunGit(sb, "git_diff", cmd, cfg, ct);
        }

        foreach (Match match in GitLogRegex.Matches(responseText))
        {
            anyExecuted = true;
            var count = match.Groups["count"].Value.Trim();
            var arg = match.Groups["arg"].Value.Trim();
            var cmd = count.Length > 0 ? $"log -n {count}" : (arg.Length > 0 ? $"log {arg}" : "log");
            await RunGit(sb, "git_log", cmd, cfg, ct);
        }

        foreach (Match match in GitAddRegex.Matches(responseText))
        {
            anyExecuted = true;
            var arg = match.Groups["arg"].Value.Trim();
            if (arg.Length == 0) continue;
            await RunGit(sb, "git_add", $"add {arg}", cfg, ct);
        }

        foreach (Match match in GitCommitRegex.Matches(responseText))
        {
            anyExecuted = true;
            var arg = match.Groups["arg"].Value.Trim();
            if (arg.Length == 0) continue;
            await RunGit(sb, "git_commit", $"commit -m {EscapeShellArg(arg)}", cfg, ct);
        }

        foreach (Match match in GitBranchRegex.Matches(responseText))
        {
            anyExecuted = true;
            var arg = match.Groups["arg"].Value.Trim();
            var cmd = arg.Length > 0 ? $"branch {arg}" : "branch";
            await RunGit(sb, "git_branch", cmd, cfg, ct);
        }

        foreach (Match match in GitPushRegex.Matches(responseText))
        {
            anyExecuted = true;
            var arg = match.Groups["arg"].Value.Trim();
            var cmd = arg.Length > 0 ? $"push {arg}" : "push";
            await RunGit(sb, "git_push", cmd, cfg, ct);
        }

        foreach (Match match in GitPullRegex.Matches(responseText))
        {
            anyExecuted = true;
            var arg = match.Groups["arg"].Value.Trim();
            var cmd = arg.Length > 0 ? $"pull {arg}" : "pull";
            await RunGit(sb, "git_pull", cmd, cfg, ct);
        }

        return anyExecuted ? sb.ToString() : null;
    }

    static async Task RunGit(StringBuilder sb, string toolName, string cmd, AppConfig cfg, CancellationToken ct)
    {
        var fullCmd = $"git {cmd}";
        var actionKey = $"git:{cmd}";

        var outcome = SafetyGate.RequestApproval(
            actionKey,
            $"Run git command: {fullCmd}",
            cfg);

        if (outcome == PermissionOutcome.Deny)
        {
            sb.AppendLine($"[Tool Result: {toolName} command=\"{fullCmd}\"]");
            sb.AppendLine("Permission denied by user.");
            sb.AppendLine();
            return;
        }

        string shell, flag;
        if (OperatingSystem.IsWindows()) { shell = "cmd.exe"; flag = "/c"; }
        else                             { shell = "/bin/sh";  flag = "-c"; }

        var psi = new ProcessStartInfo
        {
            FileName               = shell,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = Directory.GetCurrentDirectory(),
        };
        psi.ArgumentList.Add(flag);
        psi.ArgumentList.Add(fullCmd);

        AnsiConsole.MarkupLine($"[dim]$ {Markup.Escape(fullCmd)}[/]");

        var outputBuf = new StringBuilder();
        int exitCode = -1;
        Process? proc = null;

        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            var error  = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            exitCode = proc.ExitCode;

            if (!string.IsNullOrEmpty(error))
            {
                outputBuf.AppendLine(error);
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(error.TrimEnd())}[/]");
            }
            if (!string.IsNullOrEmpty(output))
            {
                outputBuf.Append(output);
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(output.TrimEnd())}[/]");
            }
        }
        catch (OperationCanceledException)
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            AnsiConsole.MarkupLine("[dim](git command interrupted)[/]");
            sb.AppendLine($"[Tool Result: {toolName} command=\"{fullCmd}\"]");
            sb.AppendLine("Command interrupted by user.");
            sb.AppendLine();
            return;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Git error:[/] {Markup.Escape(ex.Message)}");
            sb.AppendLine($"[Tool Result: {toolName} command=\"{fullCmd}\"]");
            sb.AppendLine($"Error: {ex.Message}");
            sb.AppendLine();
            return;
        }
        finally
        {
            proc?.Dispose();
        }

        string raw = outputBuf.ToString();
        if (raw.Length > 24 * 1024)
            raw = raw[..(24 * 1024)] + "\n... [output truncated]";

        sb.AppendLine($"[Tool Result: {toolName} command=\"{fullCmd}\"]");
        sb.AppendLine($"Exit Code: {exitCode}");
        sb.AppendLine("Output:");
        sb.AppendLine("```");
        sb.AppendLine(string.IsNullOrWhiteSpace(raw) ? "(no output)" : raw.TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();
    }

    static string EscapeShellArg(string arg)
    {
        if (OperatingSystem.IsWindows()) return $"\"{arg.Replace("\"", "\\\"")}\"";
        return $"'{arg.Replace("'", "'\\''")}'";
    }
}
