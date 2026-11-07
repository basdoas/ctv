
using System.Text.Json;

namespace Ctv;

/// <summary>A single todo item tracked during a session.</summary>
public sealed record TodoItem(int Id, string Text, bool IsDone);

/// <summary>
/// Session-scoped todo list. Persisted per-session name in
/// ~/.config/ctv/todos/<name>.json alongside the saved sessions.
/// </summary>
public static class Todos
{
    public static string Dir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "ctv", "todos");
        }
    }

    public static string PathFor(string name) => Path.Combine(Dir, name + ".json");

    /// <summary>Current session's todo list (loaded/saved with "current").</summary>
    public static List<TodoItem> Current { get; private set; } = new();

    static int _nextId;

    public static void Load(string name)
    {
        Current = new List<TodoItem>();
        _nextId = 1;
        if (!File.Exists(PathFor(name))) return;

        var json = File.ReadAllText(PathFor(name));
        var items = JsonSerializer.Deserialize<List<TodoItem>>(json) ?? new();
        Current = items;
        _nextId = items.Count > 0 ? items.Max(x => x.Id) + 1 : 1;
    }

    public static void Save(string name)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(PathFor(name), json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(PathFor(name), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void Add(string text)
    {
        Current.Add(new TodoItem(_nextId++, text, false));
    }

    public static void Check(int id)
    {
        var item = Current.FirstOrDefault(x => x.Id == id);
        if (item is not null)
        {
            var idx = Current.IndexOf(item);
            Current[idx] = item with { IsDone = true };
        }
    }

    public static void Uncheck(int id)
    {
        var item = Current.FirstOrDefault(x => x.Id == id);
        if (item is not null)
        {
            var idx = Current.IndexOf(item);
            Current[idx] = item with { IsDone = false };
        }
    }

    public static void Remove(int id)
    {
        Current.RemoveAll(x => x.Id == id);
    }

    public static void Clear() => Current.Clear();
}
