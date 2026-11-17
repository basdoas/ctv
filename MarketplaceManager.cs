
using System.Diagnostics;
using System.Text.Json;

namespace Ctv;

/// <summary>
/// Marketplace manager compatible with Claude Code's marketplace.json format
/// and Agent Skills specification. Manages plugin marketplaces from Git repos
/// or local paths, installs MCP servers and skills.
/// </summary>
public static class MarketplaceManager
{
    public static string Dir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "ctv", "marketplace");
        }
    }

    public static string RegistryPath => Path.Combine(Dir, "marketplaces.json");

    /// <summary>Registered marketplaces.</summary>
    public static List<MarketplaceEntry> Marketplaces
    {
        get => LoadRegistry();
        set => SaveRegistry(value);
    }

    static List<MarketplaceEntry> LoadRegistry()
    {
        if (!File.Exists(RegistryPath)) return new();
        try
        {
            var json = File.ReadAllText(RegistryPath);
            return JsonSerializer.Deserialize(json, ConfigJson.Default.ListMarketplaceEntry) ?? new();
        }
        catch { return new(); }
    }

    static void SaveRegistry(List<MarketplaceEntry> list)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(list, ConfigJson.Default.ListMarketplaceEntry);
        File.WriteAllText(RegistryPath, json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(RegistryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Add a marketplace from Git URL or local path.</summary>
    public static bool Add(string name, string url)
    {
        var list = Marketplaces;
        if (list.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;

        var localPath = Path.Combine(Dir, "repos", name.Replace('/', '_'));
        try
        {
            Directory.CreateDirectory(Path.Combine(Dir, "repos"));
            if (Directory.Exists(url))
            {
                // Local path: copy
                if (Directory.Exists(localPath)) Directory.Delete(localPath, true);
                Directory.CreateDirectory(localPath);
                CopyDirectory(url, localPath);
            }
            else
            {
                // Git clone
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    ArgumentList = { "clone", "--depth", "1", url, localPath },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                var proc = Process.Start(psi);
                proc.WaitForExit(30000);
                if (proc.ExitCode != 0) return false;
            }
        }
        catch { return false; }

        list.Add(new MarketplaceEntry { Name = name, Url = url, LocalPath = localPath, AddedAt = DateTime.UtcNow, Enabled = true });
        Marketplaces = list;
        return true;
    }

    /// <summary>Remove a marketplace.</summary>
    public static bool Remove(string name)
    {
        var list = Marketplaces;
        var entry = list.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        list.Remove(entry);
        Marketplaces = list;
        return true;
    }

    /// <summary>List all registered marketplaces.</summary>
    public static List<MarketplaceEntry> List() => Marketplaces;

    /// <summary>Browse plugins from all enabled marketplaces.</summary>
    public static List<PluginSpec> BrowseAll()
    {
        var all = new List<PluginSpec>();
        foreach (var m in Marketplaces.Where(m => m.Enabled))
        {
            try
            {
                var plugins = LoadPluginsFrom(m.LocalPath);
                foreach (var p in plugins)
                {
                    p.Repository = m.Url;
                    all.Add(p);
                }
            }
            catch { /* skip broken marketplaces */ }
        }
        return all;
    }

    /// <summary>Install a plugin: add its MCP servers to config.</summary>
    public static bool Install(string pluginName, string marketplaceName, AppConfig cfg)
    {
        var entry = Marketplaces.FirstOrDefault(m =>
            string.Equals(m.Name, marketplaceName, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !Directory.Exists(entry.LocalPath)) return false;

        var plugins = LoadPluginsFrom(entry.LocalPath);
        var plugin = plugins.FirstOrDefault(p =>
            string.Equals(p.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        if (plugin is null) return false;

        // Add MCP servers to config
        foreach (var mcp in plugin.McpServers)
        {
            if (!cfg.Mcp.Servers.Any(s => string.Equals(s.Name, mcp.Name, StringComparison.OrdinalIgnoreCase)))
                cfg.Mcp.Servers.Add(mcp);
        }

        // Add extension commands
        foreach (var cmd in plugin.Commands)
        {
            SaveExtensionCommand(cmd);
        }

        ConfigStore.Save(cfg);
        return true;
    }

    static List<PluginSpec> LoadPluginsFrom(string localPath)
    {
        var plugins = new List<PluginSpec>();
        var manifestPath = Path.Combine(localPath, ".claude-plugin", "marketplace.json");
        if (!File.Exists(manifestPath)) return plugins;

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize(json, ConfigJson.Default.MarketplaceManifest);
            if (manifest is not null)
                plugins.AddRange(manifest.Plugins);
        }
        catch { /* skip bad manifest */ }

        // Also scan for SKILL.md files (Agent Skills spec)
        try
        {
            foreach (var skillFile in Directory.GetFiles(localPath, "SKILL.md", SearchOption.AllDirectories).Take(10))
            {
                var dir = Path.GetDirectoryName(skillFile);
                var dirName = Path.GetFileName(dir ?? localPath);
                plugins.Add(new PluginSpec
                {
                    Name = $"skill-{dirName}",
                    Description = "Agent Skill (SKILL.md)",
                    Category = "skill",
                    Skills = new List<PluginSkill>
                    {
                        new PluginSkill { Name = dirName, File = skillFile }
                    }
                });
            }
        }
        catch { }

        return plugins;
    }

    static void SaveExtensionCommand(ExtensionCommand cmd)
    {
        var extDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "ctv", "extensions");
        Directory.CreateDirectory(extDir);
        var file = Path.Combine(extDir, $"{cmd.Name}.json");
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(cmd, ConfigJson.Default.ExtensionCommand));
        }
        catch { /* best effort */ }
    }

    static void CopyDirectory(string source, string dest)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), true);
    }
}
