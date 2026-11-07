
using System.Text.Json;

namespace Ctv;

/// <summary>
/// Extension system for ctv.
/// Extensions are JSON manifests in ~/.config/ctv/extensions/*.json
/// that define custom slash commands wrapping shell commands.
/// </summary>
public static class Extensions
{
    public static string Dir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "ctv", "extensions");
        }
    }

    public static List<ExtensionManifest> LoadAll()
    {
        var list = new List<ExtensionManifest>();
        if (!Directory.Exists(Dir)) return list;

        foreach (var file in Directory.GetFiles(Dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var manifest = JsonSerializer.Deserialize(json, ConfigJson.Default.ExtensionManifest);
                if (manifest is not null) list.Add(manifest);
            }
            catch { /* skip bad extensions */ }
        }
        return list;
    }

    public static List<ExtensionCommand> GetAllCommands() =>
        LoadAll().SelectMany(m => m.Commands).ToList();
}
