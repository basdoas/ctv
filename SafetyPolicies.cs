
using System.Text.RegularExpressions;

namespace Ctv;

/// <summary>
/// Additional safety policies on top of SafetyGate:
/// - Path traversal protection (confine file operations to trusted folders or cwd)
/// - Dangerous command detection (rm -rf /, sudo, mkfs, etc.)
/// - Audit log of all safety decisions
/// </summary>
public static class SafetyPolicies
{
    static readonly Regex DangerousCommandRegex = new(
        @"\b(rm\s+.*-rf|rm\s+.*--recursive.*--force|sudo\s+|mkfs|fdisk|dd\s+|shutdown|reboot|halt|poweroff|chmod\s+.*777|chown\s+.*-R)\b",
        RegexOptions.IgnoreCase);

    public static string AuditLogPath
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "ctv", "safety_audit.log");
        }
    }

    /// <summary>Check if a file path is safe to access.</summary>
    /// <param name="rawPath">User-supplied path.</param>
    /// <param name="cfg">App config.</param>
    /// <returns>(isSafe, resolvedPath, reason)</returns>
    public static (bool IsSafe, string ResolvedPath, string Reason) CheckFilePath(string rawPath, AppConfig cfg)
    {
        string resolved;
        try
        {
            resolved = Path.GetFullPath(rawPath);
        }
        catch
        {
            return (false, rawPath, "Invalid path.");
        }

        var cwd = Directory.GetCurrentDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Always allow paths inside cwd
        if (IsUnderDirectory(resolved, cwd)) return (true, resolved, "Inside working directory.");

        // Allow paths inside trusted folders
        var trusted = TrustFolders.Load();
        foreach (var t in trusted)
        {
            if (IsUnderDirectory(resolved, t)) return (true, resolved, "Inside trusted folder.");
        }

        // Allow home directory sub-paths (dotfiles, config, etc.)
        if (IsUnderDirectory(resolved, home)) return (true, resolved, "Inside home directory.");

        return (false, resolved, $"Path outside working directory and trusted folders: {resolved}");
    }

    /// <summary>Check if a shell command is dangerous.</summary>
    public static (bool IsDangerous, string Reason) CheckShellCommand(string cmd)
    {
        if (DangerousCommandRegex.IsMatch(cmd))
            return (true, $"Potentially destructive command detected: {cmd}");
        return (false, "");
    }

    static bool IsUnderDirectory(string path, string directory)
    {
        var pathNorm = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        var dirNorm = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        return pathNorm.StartsWith(dirNorm, StringComparison.OrdinalIgnoreCase);
    }

    public static void Audit(string action, string detail, bool allowed)
    {
        try
        {
            var dir = Path.GetDirectoryName(AuditLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var line = $"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] {(allowed ? "ALLOW" : "DENY")} | {action} | {detail}";
            File.AppendAllText(AuditLogPath, line + Environment.NewLine);
        }
        catch { /* best effort */ }
    }
}
