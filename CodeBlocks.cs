
using System.Text.RegularExpressions;

namespace Ctv;

/// <summary>
/// Parses fenced code blocks (```lang ... ```) from assistant responses
/// so they can be listed and expanded on demand instead of scrolling back.
/// </summary>
public sealed record CodeBlock(int Index, string Language, string Code, int LineCount);

public static class CodeBlocks
{
    static readonly Regex FenceRegex = new(
        @"```(\w*)\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Code blocks from the most recent assistant response.</summary>
    public static List<CodeBlock> Last { get; private set; } = new();

    public static List<CodeBlock> Parse(string text)
    {
        var list = new List<CodeBlock>();
        int index = 1;
        foreach (Match m in FenceRegex.Matches(text))
        {
            var lang = m.Groups[1].Value.Trim();
            var code = m.Groups[2].Value.TrimEnd();
            var lines = code.Split('\n').Length;
            list.Add(new CodeBlock(index++, lang, code, lines));
        }
        Last = list;
        return list;
    }
}
