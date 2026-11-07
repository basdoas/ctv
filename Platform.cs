
namespace Ctv;

/// <summary>
/// Cross-platform abstractions for terminal and OS operations.
/// Implementations live in Platform.Unix.cs and Platform.Windows.cs.
/// </summary>
public static class Platform
{
    /// <summary>Whether the terminal supports ANSI escape sequences.</summary>
    public static bool AnsiSupported { get; }

    /// <summary>Whether the OS is Windows.</summary>
    public static bool IsWindows { get; }

    /// <summary>Default shell for executing commands.</summary>
    public static string Shell { get; }

    /// <summary>Shell flag for running a single command string.</summary>
    public static string ShellFlag { get; }

    /// <summary>Path separator character.</summary>
    public static char PathSeparator { get; }

    /// <summary>Home directory path.</summary>
    public static string HomeDirectory { get; }

    /// <summary>Desktop directory path (handles localized names like Masaüstü).</summary>
    public static string DesktopDirectory { get; }

    static Platform()
    {
        IsWindows = OperatingSystem.IsWindows();
        AnsiSupported = DetectAnsiSupport();
        Shell = IsWindows ? "cmd.exe" : "/bin/sh";
        ShellFlag = IsWindows ? "/c" : "-c";
        PathSeparator = IsWindows ? '\\' : '/';
        HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        DesktopDirectory = GetDesktopDirectory();
    }

    static bool DetectAnsiSupport()
    {
        if (IsWindows) return false; // Windows console needs explicit ANSI mode
        var term = Environment.GetEnvironmentVariable("TERM") ?? "";
        return !string.IsNullOrEmpty(term) && term != "dumb";
    }

    static string GetDesktopDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
            return desktop;

        var home = HomeDirectory;
        var localized = Path.Combine(home, "Masaüstü");
        if (Directory.Exists(localized)) return localized;
        return Path.Combine(home, "Desktop");
    }

    /// <summary>Resolve a path, expanding ~, ~/..., Desktop/, etc.</summary>
    public static string ResolvePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return "";
        string p = rawPath.Trim().Trim('"', '\'');

        if (p == "~" || p == "$HOME") return HomeDirectory;

        if (p.StartsWith("~/") || p.StartsWith("~\\"))
        {
            var rest = p[2..];
            if (rest.StartsWith("Desktop/", StringComparison.OrdinalIgnoreCase) ||
                rest.StartsWith("Desktop\\", StringComparison.OrdinalIgnoreCase) ||
                rest.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
            {
                var sub = rest.Length > 7 ? rest[8..] : "";
                return Path.Combine(DesktopDirectory, sub);
            }
            return Path.Combine(HomeDirectory, rest);
        }

        if (p.StartsWith("Desktop/", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("Desktop\\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(DesktopDirectory, p[8..]);
        }

        return Path.GetFullPath(p);
    }

    /// <summary>Set file permissions to owner read/write only (Unix). No-op on Windows.</summary>
    public static void SetOwnerReadWrite(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
    }

    /// <summary>Escape a shell argument for the current platform.</summary>
    public static string EscapeShellArg(string arg)
    {
        if (IsWindows) return $"\"{arg.Replace("\"", "\\\"")}\"";
        return $"'{arg.Replace("'", "'\\''")}'";
    }
}
