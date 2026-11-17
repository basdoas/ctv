
using System.Text;
using System.Text.Json;

namespace Ctv;

/// <summary>
/// Chat compression: summarizes older conversation history into a compact block
/// when token count exceeds a threshold, keeping the conversation going without
/// hitting context limits. Similar to Mistral Vibe's context compression.
/// </summary>
public static class ChatCompressor
{
    /// <summary>Compress conversation history if token count exceeds maxTokens.
    /// Returns compressed history, or original if no compression needed.</summary>
    public static List<ChatMessage> Compress(List<ChatMessage> history, int maxTokens)
    {
        if (history.Count <= 2) return history;

        int currentTokens = Tokenizer.EstimateHistory(history);
        if (currentTokens <= maxTokens) return history;

        // Keep system + first user message + last N messages, summarize the middle
        var keepHead = 2;  // system + first user
        var keepTail = 3;  // last 3 messages
        var middle = history.Skip(keepHead).Take(history.Count - keepHead - keepTail).ToList();

        if (middle.Count == 0) return history;

        // Build summary of middle messages
        var userMsgs = middle.Where(m => m.Role == "user").Select(m => m.Content).ToList();
        var assistantMsgs = middle.Where(m => m.Role == "assistant").Select(m => m.Content).ToList();

        var summary = new StringBuilder();
        summary.AppendLine("[Previous conversation summary]");
        if (userMsgs.Count > 0)
        {
            summary.AppendLine("User asked about:");
            foreach (var u in userMsgs.Take(5))
                summary.AppendLine("- " + Tokenizer.Truncate(u, 100));
        }
        if (assistantMsgs.Count > 0)
        {
            summary.AppendLine("Assistant responded:");
            foreach (var a in assistantMsgs.Take(5))
                summary.AppendLine("- " + Tokenizer.Truncate(a, 100));
        }
        summary.AppendLine($"[{middle.Count} messages compressed into this summary]");

        var compressed = new List<ChatMessage>();
        // Keep head
        foreach (var m in history.Take(keepHead)) compressed.Add(m);
        // Insert compressed summary as a system message
        compressed.Add(new ChatMessage("system", summary.ToString()));
        // Keep tail
        foreach (var m in history.Skip(history.Count - keepTail)) compressed.Add(m);

        return compressed;
    }
}
