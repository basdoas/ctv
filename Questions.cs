
using System.Text.Json;

namespace Ctv;

/// <summary>A question the AI has asked the user, awaiting an answer.</summary>
public sealed record PendingQuestion(
    string Id,
    string Question,
    List<string> Options,   // empty = free text
    bool AllowOther
);

/// <summary>
/// Questions the AI has asked mid-turn that are waiting for user answers.
/// Persisted with the session so they survive across restarts.
/// </summary>
public static class Questions
{
    public static string Dir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "ctv", "questions");
        }
    }

    public static string PathFor(string name) => Path.Combine(Dir, name + ".json");

    /// <summary>Current session's pending questions.</summary>
    public static List<PendingQuestion> Current { get; private set; } = new();

    public static void Load(string name)
    {
        Current = new List<PendingQuestion>();
        if (!File.Exists(PathFor(name))) return;
        var json = File.ReadAllText(PathFor(name));
        Current = JsonSerializer.Deserialize(json, ConfigJson.Default.ListPendingQuestion) ?? new();
    }

    public static void Save(string name)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(Current, ConfigJson.Default.ListPendingQuestion);
        File.WriteAllText(PathFor(name), json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(PathFor(name), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void Add(PendingQuestion q) => Current.Add(q);

    public static PendingQuestion? Get(string id) => Current.FirstOrDefault(q => q.Id == id);

    public static void Remove(string id) => Current.RemoveAll(q => q.Id == id);

    public static void Clear() => Current.Clear();
}
