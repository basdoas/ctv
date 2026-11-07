
using System.Text.Json;

namespace Ctv;

/// <summary>
/// Shared, persistent memories that survive across sessions.
/// Stored in ~/.config/ctv/memories.json and injected into every system prompt.
/// </summary>
public static class Memories
{
    public static string Dir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "ctv");
        }
    }

    public static string FilePath => Path.Combine(Dir, "memories.json");

    public static List<string> Load()
    {
        if (!File.Exists(FilePath)) return new();
        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize(json, ConfigJson.Default.ListString) ?? new();
    }

    public static void Save(List<string> memories)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(memories, ConfigJson.Default.ListString);
        File.WriteAllText(FilePath, json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void Add(string memory)
    {
        var list = Load();
        if (!list.Contains(memory))
        {
            list.Add(memory);
            Save(list);
        }
    }

    public static void Clear() => Save(new List<string>());
}
