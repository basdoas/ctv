
using System.Text;
using Spectre.Console;

namespace Ctv;

public static class QuestionCommands
{
    public static void HandleQuestions(AppConfig cfg, List<ChatMessage> history)
    {
        var pending = Questions.Current.Where(q => !string.IsNullOrEmpty(q.Question)).ToList();
        if (pending.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No pending questions.[/]");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[bold]Pending questions:[/]");
        for (int i = 0; i < pending.Count; i++)
        {
            var q = pending[i];
            sb.AppendLine($"  {i + 1}. {Markup.Escape(q.Question)}");
            if (q.Options.Count > 0)
            {
                for (int j = 0; j < q.Options.Count; j++)
                    sb.AppendLine($"     [{j + 1}] {Markup.Escape(q.Options[j])}");
                if (q.AllowOther) sb.AppendLine($"     [0] Other (free text)");
            }
            else
            {
                sb.AppendLine($"     [free text]");
            }
        }
        AnsiConsole.WriteLine();
        AnsiConsole.Markup(sb.ToString());

        while (true)
        {
            AnsiConsole.Markup("Answer which question number (or 'q' to quit): ");
            var input = Console.ReadLine()?.Trim();
            if (input is null || input.Equals("q", StringComparison.OrdinalIgnoreCase)) return;

            if (!int.TryParse(input, out var qIdx) || qIdx < 1 || qIdx > pending.Count)
            {
                AnsiConsole.MarkupLine("[red]Invalid number.[/]");
                continue;
            }

            var q = pending[qIdx - 1];
            string answer;

            if (q.Options.Count > 0)
            {
                AnsiConsole.Markup($"Choose option (1-{q.Options.Count}" + (q.AllowOther ? ", 0 for other" : "") + "): ");
                var optInput = Console.ReadLine()?.Trim();
                if (optInput is null) return;

                if (q.AllowOther && optInput == "0")
                {
                    AnsiConsole.Markup("Your answer: ");
                    answer = Console.ReadLine() ?? "";
                }
                else if (int.TryParse(optInput, out var optIdx) && optIdx >= 1 && optIdx <= q.Options.Count)
                {
                    answer = q.Options[optIdx - 1];
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Invalid option.[/]");
                    continue;
                }
            }
            else
            {
                AnsiConsole.Markup("Your answer: ");
                answer = Console.ReadLine() ?? "";
            }

            // Feed the answer back to the conversation
            var answerMsg = $"[User answered question '{q.Id}']: {answer}";
            history.Add(new ChatMessage("user", answerMsg));
            Questions.Remove(q.Id);
            Questions.Save("current");
            Sessions.Save("current", history);
            AnsiConsole.MarkupLine($"[green]Answer recorded.[/]");

            // Re-enter the AI loop by returning a special marker? 
            // For now, just tell the user to continue the conversation.
            AnsiConsole.MarkupLine("[dim]The AI will see your answer on the next turn. Press Enter to continue chatting.[/]");
            Console.ReadLine();
            return;
        }
    }
}
