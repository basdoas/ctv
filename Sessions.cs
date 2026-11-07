
using System.Text.Json;

namespace Ctv;

public static class Sessions
{
    public static string Dir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "ctv", "sessions");
        }
    }

    public static string PathFor(string name) => Path.Combine(Dir, name + ".json");

    public static void Save(string name, List<ChatMessage> history)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(history, ConfigJson.Default.ListChatMessage);
        File.WriteAllText(PathFor(name), json);
    }

    public static List<ChatMessage>? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<ChatMessage>>(json, ConfigJson.Default.ListChatMessage);
    }

    public static List<string> List()
    {
        if (!Directory.Exists(Dir)) return new();
        return Directory.GetFiles(Dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderByDescending(x => File.GetLastWriteTimeUtc(PathFor(x)))
            .ToList();
    }

    public static void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }
}

