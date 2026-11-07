
using System.Text.Json;

namespace Ctv;

/// <summary>
/// Trust folder system — like Mistral Vibe's trusted folders.
/// When the user runs ctv in a new directory, it asks whether to trust it.
/// Trusted folders are remembered in ~/.config/ctv/trusted_folders.json.
/// </summary>
public static class TrustFolders
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

    public static string FilePath => Path.Combine(Dir, "trusted_folders.json");

    public static List<string> Load()
    {
        if (!File.Exists(FilePath)) return new();
        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize(json, ConfigJson.Default.ListString) ?? new();
    }

    public static void Save(List<string> folders)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(folders, ConfigJson.Default.ListString);
        File.WriteAllText(FilePath, json);
        Platform.SetOwnerReadWrite(FilePath);
    }

    public static bool IsTrusted(string folder)
    {
        var resolved = Path.GetFullPath(folder);
        return Load().Contains(resolved, StringComparer.OrdinalIgnoreCase);
    }

    public static void Trust(string folder)
    {
        var resolved = Path.GetFullPath(folder);
        var list = Load();
        if (!list.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(resolved);
            Save(list);
        }
    }

    public static void Untrust(string folder)
    {
        var resolved = Path.GetFullPath(folder);
        var list = Load();
        list.RemoveAll(f => string.Equals(f, resolved, StringComparison.OrdinalIgnoreCase));
        Save(list);
    }

    public static void Clear() => Save(new List<string>());
}
