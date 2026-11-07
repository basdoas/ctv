
using System.Text;
using System.Text.RegularExpressions;

namespace Ctv;

/// <summary>
/// Parses <ask_question> XML tags from AI responses:
///
///   <ask_question id="q1" type="choice">
///     <question>What's the main goal?</question>
///     <option label="Performance">Make it run faster</option>
///     <option label="Readability">Make it easier to understand</option>
///     <other>true</other>
///   </ask_question>
///
///   <ask_question id="q2" type="text">
///     <question>What's your API key?</question>
///   </ask_question>
///
/// Returns the list of parsed questions so the UI can prompt the user.
/// </summary>
public static class QuestionTools
{
    static readonly Regex AskQuestionRegex = new(
        @"<ask_question\s+id=[""'](?<id>[^""']+)[""']\s+type=[""'](?<type>choice|text)[""']\s*>(?<body>.*?)</ask_question>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    static readonly Regex QuestionRegex = new(@"<question>(?<text>.*?)</question>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex OptionRegex     = new(@"<option\s+label=[""'](?<label>[^""']+)[""']\s*>(?<desc>.*?)</option>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex OtherRegex      = new(@"<other>(?<val>true|false)</other>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static bool HasQuestionCalls(string text) => AskQuestionRegex.IsMatch(text);

    public static List<PendingQuestion> ParseQuestions(string responseText)
    {
        var list = new List<PendingQuestion>();
        foreach (Match m in AskQuestionRegex.Matches(responseText))
        {
            var id = m.Groups["id"].Value.Trim();
            var type = m.Groups["type"].Value.Trim().ToLowerInvariant();
            var body = m.Groups["body"].Value;

            var qMatch = QuestionRegex.Match(body);
            var question = qMatch.Success ? qMatch.Groups["text"].Value.Trim() : "";

            var options = new List<string>();
            bool allowOther = false;

            if (type == "choice")
            {
                foreach (Match om in OptionRegex.Matches(body))
                {
                    var label = om.Groups["label"].Value.Trim();
                    var desc = om.Groups["desc"].Value.Trim();
                    options.Add(string.IsNullOrEmpty(desc) ? label : $"{label} — {desc}");
                }

                var oMatch = OtherRegex.Match(body);
                if (oMatch.Success) allowOther = oMatch.Groups["val"].Value == "true";
                allowOther = allowOther || options.Count == 0; // always allow other if no options
            }

            list.Add(new PendingQuestion(id, question, options, allowOther));
        }
        return list;
    }
}
