using System.Diagnostics;
using System.Text;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Handles <c>!command</c> messages: runs the shell command (gated by
/// <see cref="SafetyGate"/>), streams its output to the terminal, then
/// returns a formatted string that gets sent to the AI so it can reason
/// about the output.
///
/// Syntax options:
///   <c>!ls -la</c>                  → runs command, asks AI to describe output
///   <c>!git diff HEAD~1    explain the changes</c>  → runs command, appends custom question
///
/// Everything before the first double-space (or end of line) is the shell
/// command; anything after is the question to ask the AI.
/// </summary>
public static class ToolRunner
{
    const int MaxOutputBytes = 24 * 1024; // 24 KB max capture

    /// <summary>
    /// If <paramref name="userMessage"/> starts with <c>!</c>, extracts the
    /// shell command and an optional follow-up question, gates execution through
    /// <see cref="SafetyGate"/>, runs the command, and returns a formatted AI
    /// prompt.  Returns <c>null</c> if the message is not a command or was denied.
    /// </summary>
    public static async Task<string?> TryRunAsync(
        string userMessage, AppConfig cfg, CancellationToken ct)
    {
        if (!userMessage.StartsWith('!')) return null;

        var body = userMessage[1..]; // strip leading '!'
        string cmd, question;
        var sep = body.IndexOf("  ", StringComparison.Ordinal);
        if (sep >= 0)
        {
            cmd      = body[..sep].Trim();
            question = body[(sep + 2)..].Trim();
        }
        else
        {
            cmd      = body.Trim();
            question = "";
        }

        if (cmd.Length == 0) return null;

        var (allowed, exitCode, raw) = await ExecuteCommandAsync(cmd, cfg, ct);
        if (!allowed) return null;

        // ── Build AI prompt ───────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine($"I ran the following shell command (exit code {exitCode}):");
        sb.AppendLine($"```sh");
        sb.AppendLine(cmd);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Output:");
        sb.AppendLine("```");
        sb.Append(raw);
        sb.AppendLine("```");

        if (question.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(question);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Please analyse this output and summarise what it means.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Executes a shell command after safety check, streaming output to console and returning the result.
    /// </summary>
    public static async Task<(bool Allowed, int ExitCode, string Output)> ExecuteCommandAsync(
        string cmd, AppConfig cfg, CancellationToken ct)
    {
        cmd = cmd.Trim();
        if (cmd.Length == 0) return (false, -1, "Empty command.");

        var outcome = SafetyGate.RequestApproval(
            SafetyGate.ShellKey(cmd),
            $"Run shell command: {cmd}",
            cfg);

        if (outcome == PermissionOutcome.Deny)
            return (false, -1, "User denied permission to run this command.");

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
        psi.ArgumentList.Add(cmd);

        AnsiConsole.MarkupLine($"[dim]$ {Markup.Escape(cmd)}[/]");
        AnsiConsole.Write(new Rule().RuleStyle("dim"));

        var outputBuf = new StringBuilder();
        int exitCode  = -1;

        Process? proc = null;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start process.");

            var readOut = ConsumeStream(proc.StandardOutput, outputBuf, ct);
            var readErr = ConsumeStream(proc.StandardError,  outputBuf, ct);

            await Task.WhenAll(readOut, readErr);
            await proc.WaitForExitAsync(ct);
            exitCode = proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            AnsiConsole.MarkupLine("[dim](command interrupted)[/]");
            return (true, -1, "Command interrupted by user.");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to run command:[/] {Markup.Escape(ex.Message)}");
            return (true, -1, $"Failed to run command: {ex.Message}");
        }
        finally
        {
            proc?.Dispose();
        }

        AnsiConsole.Write(new Rule().RuleStyle("dim"));

        string raw = outputBuf.ToString();
        if (raw.Length > MaxOutputBytes)
        {
            raw = raw[..MaxOutputBytes] + "\n... [output truncated]";
        }

        return (true, exitCode, raw);
    }

    // Reads a stream line-by-line, writes each line to the terminal (dim),
    // and appends it to the shared buffer.  Stops once buffer is full.
    static async Task ConsumeStream(
        System.IO.StreamReader reader,
        StringBuilder          buf,
        CancellationToken      ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch (OperationCanceledException) { break; }

            if (line is null) break;

            // Print live to terminal.
            Console.WriteLine(line);

            lock (buf)
            {
                if (buf.Length < 24 * 1024)
                    buf.AppendLine(line);
            }
        }
    }
}
