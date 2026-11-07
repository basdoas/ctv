
using System.Text;
using System.Text.RegularExpressions;

namespace Ctv;

/// <summary>
/// Parses todo XML tool calls from AI responses:
///   <todo_add>Buy milk</todo_add>
///   <todo_check>1</todo_check>
///   <todo_uncheck>1</todo_uncheck>
///   <todo_remove>1</todo_remove>
///   <todo_list />
///
/// Returns a formatted result string that gets sent back to the AI
/// so it sees the updated todo list.
/// </summary>
public static class TodoTools
{
    static readonly Regex TodoAddRegex    = new(@"<todo_add>(?<text>.*?)</todo_add>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex TodoCheckRegex  = new(@"<todo_check\s+id=[""'](?<id>\d+)[""']\s*/?>|<todo_check>(?<id>\d+)</todo_check>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex TodoUncheckRegex= new(@"<todo_uncheck\s+id=[""'](?<id>\d+)[""']\s*/?>|<todo_uncheck>(?<id>\d+)</todo_uncheck>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex TodoRemoveRegex = new(@"<todo_remove\s+id=[""'](?<id>\d+)[""']\s*/?>|<todo_remove>(?<id>\d+)</todo_remove>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex TodoListRegex   = new(@"<todo_list\s*/>|<todo_list>(?<arg>.*?)</todo_list>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static bool HasTodoCalls(string text) =>
        TodoAddRegex.IsMatch(text) || TodoCheckRegex.IsMatch(text) || TodoUncheckRegex.IsMatch(text) ||
        TodoRemoveRegex.IsMatch(text) || TodoListRegex.IsMatch(text);

    public static string ExecuteTodoCalls(string responseText)
    {
        if (!HasTodoCalls(responseText)) return "";

        var sb = new StringBuilder();
        bool anyExecuted = false;

        foreach (Match match in TodoAddRegex.Matches(responseText))
        {
            anyExecuted = true;
            var text = match.Groups["text"].Value.Trim();
            if (text.Length == 0) continue;
            Todos.Add(text);
            sb.AppendLine($"[Tool Result: todo_add text=\"{text}\"]");
            sb.AppendLine($"Added todo #{Todos.Current.Last().Id}: {text}");
            sb.AppendLine();
        }

        foreach (Match match in TodoCheckRegex.Matches(responseText))
        {
            anyExecuted = true;
            var id = int.Parse(match.Groups["id"].Value);
            Todos.Check(id);
            sb.AppendLine($"[Tool Result: todo_check id=\"{id}\"]");
            sb.AppendLine($"Marked todo #{id} as done.");
            sb.AppendLine();
        }

        foreach (Match match in TodoUncheckRegex.Matches(responseText))
        {
            anyExecuted = true;
            var id = int.Parse(match.Groups["id"].Value);
            Todos.Uncheck(id);
            sb.AppendLine($"[Tool Result: todo_uncheck id=\"{id}\"]");
            sb.AppendLine($"Marked todo #{id} as not done.");
            sb.AppendLine();
        }

        foreach (Match match in TodoRemoveRegex.Matches(responseText))
        {
            anyExecuted = true;
            var id = int.Parse(match.Groups["id"].Value);
            Todos.Remove(id);
            sb.AppendLine($"[Tool Result: todo_remove id=\"{id}\"]");
            sb.AppendLine($"Removed todo #{id}.");
            sb.AppendLine();
        }

        // Always include current list state if any todo action happened
        if (anyExecuted || TodoListRegex.IsMatch(responseText))
        {
            sb.AppendLine("[Tool Result: todo_list]");
            sb.AppendLine("Current todos:");
            if (Todos.Current.Count == 0)
                sb.AppendLine("(none)");
            else
            {
                foreach (var t in Todos.Current)
                    sb.AppendLine($"  [{(t.IsDone ? "x" : " ")}] {t.Id}. {t.Text}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
