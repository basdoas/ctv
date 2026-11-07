namespace Ctv;

public static class PathResolver
{
    public static string GetDesktopPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        {
            var turkish = Path.Combine(home, "Masaüstü");
            if (Directory.Exists(turkish)) return turkish;
            return Path.Combine(home, "Desktop");
        }
        return desktop;
    }

    public static string Resolve(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return "";
        string p = rawPath.Trim().Trim('"', '\'');

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = GetDesktopPath();

        if (p == "~" || p == "$HOME") return home;

        if (p.StartsWith("~/") || p.StartsWith("~\\"))
        {
            var rest = p[2..];
            if (rest.StartsWith("Desktop/", StringComparison.OrdinalIgnoreCase) ||
                rest.StartsWith("Desktop\\", StringComparison.OrdinalIgnoreCase) ||
                rest.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
            {
                var sub = rest.Length > 7 ? rest[8..] : "";
                return Path.Combine(desktop, sub);
            }
            return Path.Combine(home, rest);
        }

        if (p.StartsWith("Desktop/", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("Desktop\\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(desktop, p[8..]);
        }

        return Path.GetFullPath(p);
    }
}
