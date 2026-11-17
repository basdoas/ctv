
using System;

namespace Ctv;

/// <summary>
/// Rough token estimator: ~4 characters per token for typical text.
/// Not a real tokenizer (no BPE/tiktoken), but good enough for context warnings.
/// </summary>
public static class Tokenizer
{
    /// <summary>Approximate token count for a string (chars / 4, rounded up).</summary>
    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (text.Length + 3) / 4;
    }

    /// <summary>Estimate total tokens in a chat history.</summary>
    public static int EstimateHistory(List<ChatMessage> history)
    {
        int total = 0;
        foreach (var m in history)
        {
            // ~4 tokens per message overhead (role, formatting, etc.)
            total += 4 + Estimate(m.Content);
        }
        return total;
    }

    /// <summary>Format token count for display (e.g. "1.2K").</summary>
    public static string Format(int tokens)
    {
        if (tokens >= 1000) return $"{(tokens / 1000.0):0.0}K";
        return tokens.ToString();
    }
}
