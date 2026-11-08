using System.Text;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Handles <c>@path</c> tokens embedded in a user message.
///
/// Any word that starts with <c>@</c> is treated as a file path.
/// The file's content is prepended to the message as a fenced code block
/// so the AI has full context.
///
/// Example:  <c>@./src/Program.cs what does this do?</c>
/// </summary>
public static class FileAttachment
{
    /// Maximum bytes read per file before truncation.
    const int MaxBytes = 32 * 1024;

    /// <summary>
    /// Scans <paramref name="userMessage"/> for <c>@path</c> tokens,
    /// gates each read through <see cref="SafetyGate"/>, and returns the
    /// augmented message, or <c>null</c> when the user denies a read.
    /// Returns the original message unchanged if no <c>@</c> tokens are found.
    /// </summary>
    public static string? ProcessAttachments(string userMessage, AppConfig cfg)
    {
        var tokens   = userMessage.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var blocks   = new StringBuilder();
        var rest     = new List<string>();
        bool anyFile = false;

        foreach (var token in tokens)
        {
            if (!token.StartsWith('@'))
            {
                rest.Add(token);
                continue;
            }

            var rawPath = token[1..];
            if (rawPath.Length == 0)
            {
                rest.Add(token);
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Platform.ResolvePath(rawPath);
            }
            catch
            {
                rest.Add(token);
                continue;
            }

            if (!File.Exists(fullPath))
            {
                if (rawPath.StartsWith("./") || rawPath.StartsWith("../") || rawPath.StartsWith("/") ||
                    rawPath.Contains(Path.DirectorySeparatorChar) || Path.HasExtension(rawPath))
                {
                    AnsiConsole.MarkupLine($"[yellow]File not found:[/] {Markup.Escape(rawPath)}");
                    anyFile = true;
                }
                else
                {
                    // Likely a regular @mention or word, preserve it
                    rest.Add(token);
                }
                continue;
            }

            anyFile = true;

            // Gate: use the generic "file:read" key so one approval covers all reads.
            var outcome = SafetyGate.RequestApproval(
                SafetyGate.FileReadKey,
                $"Read file: {fullPath}",
                cfg);

            if (outcome == PermissionOutcome.Deny) return null;

            try
            {
                var info = new FileInfo(fullPath);

                string content;
                if (info.Length > MaxBytes)
                {
                    AnsiConsole.MarkupLine(
                        $"[dim]Truncating {Markup.Escape(info.Name)} to {MaxBytes / 1024} KB[/]");
                    using var fs = File.OpenRead(fullPath);
                    var buf  = new byte[MaxBytes];
                    int read = fs.Read(buf, 0, MaxBytes);
                    content  = Encoding.UTF8.GetString(buf, 0, read) + "\n... [truncated]";
                }
                else
                {
                    content = File.ReadAllText(fullPath);
                }

                var lang = info.Extension.TrimStart('.').ToLowerInvariant();
                blocks.AppendLine($"```{lang}");
                blocks.AppendLine($"// {fullPath}");
                blocks.AppendLine(content.TrimEnd());
                blocks.AppendLine("```");
                blocks.AppendLine();

                AnsiConsole.MarkupLine(
                    $"[dim]attached {Markup.Escape(info.Name)} ({info.Length:#,0} bytes)[/]");
            }
            catch (UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine($"[red]Access denied:[/] {Markup.Escape(fullPath)}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Cannot read {Markup.Escape(rawPath)}:[/] {Markup.Escape(ex.Message)}");
            }
        }

        if (!anyFile) return userMessage; // fast path — nothing to do

        var question = string.Join(' ', rest).Trim();
        if (blocks.Length == 0) return question; // all reads failed/denied

        return blocks.ToString() + (question.Length > 0 ? question : "");
    }
}
