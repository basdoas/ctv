using System.Text;

namespace Ctv;

public static class QuestionCommands
{
    /// <summary>
    /// Asks the user every pending question with native dialogs and returns the answers formatted for the model.
    /// Returns null when there was nothing to answer or the user cancelled before answering anything.
    /// Answered questions are removed; the rest stay pending (use /questions to answer them later).
    /// </summary>
    public static string? Run()
    {
        var pending = Questions.Current.Where(q => !string.IsNullOrEmpty(q.Question)).ToList();
        if (pending.Count == 0) return null;

        const string other = "Other (type my own answer)";
        var sb = new StringBuilder();
        try
        {
            foreach (var q in pending)
            {
                string answer;
                if (q.Options.Count > 0)
                {
                    var options = q.Options.ToList();
                    if (q.AllowOther) options.Add(other);
                    var picked = Ui.Choose(q.Question, options);
                    answer = picked == other ? Ui.Ask(q.Question) : picked;
                }
                else
                {
                    answer = Ui.Ask(q.Question);
                }

                sb.AppendLine($"[User answered question '{q.Id}']: {answer}");
                Questions.Remove(q.Id);
            }
        }
        catch (OperationCanceledException)
        {
            // The user pressed Esc: whatever is still unanswered stays pending.
        }
        finally
        {
            Questions.Save("current");
        }

        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }
}
