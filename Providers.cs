using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Ctv;

public sealed record ChatMessage(string Role, string Content); // Role: "user" | "assistant"

public interface IChatProvider
{
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> history, string system, CancellationToken ct);
}

public static class Providers
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static IChatProvider Create(ProviderConfig p) => p.Kind switch
    {
        "anthropic" => new AnthropicProvider(p),
        "gemini" => new GeminiProvider(p),
        _ => new OpenAiCompatProvider(p),
    };

    /// <summary>Sends the request and yields the payload of every SSE "data:" line.</summary>
    internal static async IAsyncEnumerable<string> PostSse(HttpRequestMessage req, [EnumeratorCancellation] CancellationToken ct)
    {
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
                yield return line[5..].Trim();
        }
    }

    // Hand-written JSON (Utf8JsonWriter) keeps everything reflection-free, so Native AOT is happy.
    internal static StringContent Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) write(w);
        return new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json");
    }

    /// <summary>Asks the provider which models it offers (so defaults never go stale).</summary>
    public static async Task<List<string>> ListModelsAsync(ProviderConfig p, CancellationToken ct = default)
    {
        var key = ConfigStore.ResolveKey(p);
        var baseUrl = p.BaseUrl.TrimEnd('/');
        HttpRequestMessage req;
        switch (p.Kind)
        {
            case "anthropic":
                req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/models?limit=100");
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", "2023-06-01");
                break;
            case "gemini":
                req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1beta/models?pageSize=200");
                req.Headers.Add("x-goog-api-key", key);
                break;
            default:
                req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
                if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                break;
        }

        using (req)
        using (var resp = await Http.SendAsync(req, ct))
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var ids = new List<string>();
            if (p.Kind == "gemini")
            {
                if (doc.RootElement.TryGetProperty("models", out var ms))
                    foreach (var m in ms.EnumerateArray())
                    {
                        var name = m.GetProperty("name").GetString() ?? "";
                        ids.Add(name.StartsWith("models/", StringComparison.Ordinal) ? name[7..] : name);
                    }
            }
            else if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.GetString() is { } s)
                        ids.Add(s);
            }
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return ids;
        }
    }
}

/// <summary>OpenAI, NVIDIA NIM, Mistral, OpenRouter, Groq, Ollama, LM Studio... anything speaking /chat/completions.</summary>
sealed class OpenAiCompatProvider(ProviderConfig p) : IChatProvider
{
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> history, string system, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, p.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = Providers.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("model", p.Model);
                w.WriteBoolean("stream", true);
                w.WriteStartArray("messages");
                w.WriteStartObject(); w.WriteString("role", "system"); w.WriteString("content", system); w.WriteEndObject();
                foreach (var m in history)
                {
                    w.WriteStartObject(); w.WriteString("role", m.Role); w.WriteString("content", m.Content); w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            })
        };
        var key = ConfigStore.ResolveKey(p);
        if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        await foreach (var data in Providers.PostSse(req, ct))
        {
            if (data == "[DONE]") yield break;
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                yield return c.GetString()!;
        }
    }
}

sealed class AnthropicProvider(ProviderConfig p) : IChatProvider
{
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> history, string system, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, p.BaseUrl.TrimEnd('/') + "/v1/messages")
        {
            Content = Providers.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("model", p.Model);
                w.WriteNumber("max_tokens", 4096);
                w.WriteBoolean("stream", true);
                w.WriteString("system", system);
                w.WriteStartArray("messages");
                foreach (var m in history)
                {
                    w.WriteStartObject(); w.WriteString("role", m.Role); w.WriteString("content", m.Content); w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            })
        };
        req.Headers.Add("x-api-key", ConfigStore.ResolveKey(p));
        req.Headers.Add("anthropic-version", "2023-06-01");

        await foreach (var data in Providers.PostSse(req, ct))
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type)) continue;
            switch (type.GetString())
            {
                case "content_block_delta"
                    when root.TryGetProperty("delta", out var d)
                         && d.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String:
                    yield return t.GetString()!;
                    break;
                case "error":
                    throw new InvalidOperationException(root.GetRawText());
            }
        }
    }
}

sealed class GeminiProvider(ProviderConfig p) : IChatProvider
{
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> history, string system, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = $"{p.BaseUrl.TrimEnd('/')}/v1beta/models/{p.Model}:streamGenerateContent?alt=sse";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = Providers.Json(w =>
            {
                w.WriteStartObject();
                w.WriteStartObject("systemInstruction");
                w.WriteStartArray("parts");
                w.WriteStartObject(); w.WriteString("text", system); w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteStartArray("contents");
                foreach (var m in history)
                {
                    w.WriteStartObject();
                    w.WriteString("role", m.Role == "assistant" ? "model" : "user");
                    w.WriteStartArray("parts");
                    w.WriteStartObject(); w.WriteString("text", m.Content); w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            })
        };
        req.Headers.Add("x-goog-api-key", ConfigStore.ResolveKey(p));

        await foreach (var data in Providers.PostSse(req, ct))
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0) continue;
            if (!cands[0].TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)) continue;
            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    yield return t.GetString()!;
        }
    }
}
