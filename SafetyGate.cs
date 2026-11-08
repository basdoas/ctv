using Spectre.Console;

namespace Ctv;

/// <summary>Returned by <see cref="SafetyGate.RequestApproval"/>.</summary>
public enum PermissionOutcome { Allow, Deny }

/// <summary>
/// Central permission gate for file reads and shell commands.
///
/// Permission levels (checked in order):
///   1. AutoApprove flag in config  → always allow
///   2. Session-allow-all           → always allow (until process exits)
///   3. Persistent AllowedActions   → allow if action key is in the list
///   4. Session allow-list          → allow if added earlier this session
///   5. Interactive prompt          → ask the user, record the answer
/// </summary>
public static class SafetyGate
{
    // In-memory, cleared on /clear or process exit.
    static readonly HashSet<string> _sessionAllowed =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _turnAllowed =
        new(StringComparer.OrdinalIgnoreCase);
    static bool _sessionAllowAll;

    // ── Action key helpers ─────────────────────────────────────────────────
    /// <summary>Key used for any file-read action (not path-specific).</summary>
    public const string FileReadKey  = "file:read";
    /// <summary>Key for a specific shell command string.</summary>
    public static string ShellKey(string cmd) => $"shell:{cmd}";

    // ── Main entry point ──────────────────────────────────────────────────
    /// <param name="action">Normalised key string stored in the allow-list.</param>
    /// <param name="label">Human-readable description shown in the prompt.</param>
    public static PermissionOutcome RequestApproval(
        string action, string label, AppConfig cfg)
    {
        // Fast-path checks (no UI).
        if (cfg.AutoApprove || _sessionAllowAll) return PermissionOutcome.Allow;
        if (cfg.AllowedActions.Contains(action, StringComparer.OrdinalIgnoreCase))
            return PermissionOutcome.Allow;
        if (_sessionAllowed.Contains(action) || _turnAllowed.Contains(action))
            return PermissionOutcome.Allow;

        if (Console.IsInputRedirected)
        {
            // Non-interactive fallback
            return PermissionOutcome.Deny;
        }

        return VibeScreen.ShowApprovalModal(action, label, cfg);
    }

    // ── Session management ────────────────────────────────────────────────
    /// <summary>Resets turn-scoped permissions at the start of an agent step.</summary>
    public static void ResetTurn()
    {
        _turnAllowed.Clear();
    }

    /// <summary>Remembers an action for the rest of this session ("Allow for this session").</summary>
    public static void AllowForSession(string action) => _sessionAllowed.Add(action);

    /// <summary>Drops all session-scoped permissions (called by /clear).</summary>
    public static void ClearSession()
    {
        _turnAllowed.Clear();
        _sessionAllowed.Clear();
        _sessionAllowAll = false;
    }

    /// <summary>Remove one entry from the persistent allow-list.</summary>
    public static void RemoveAllowedAction(string action, AppConfig cfg)
    {
        cfg.AllowedActions.RemoveAll(a =>
            string.Equals(a, action, StringComparison.OrdinalIgnoreCase));
        ConfigStore.Save(cfg);
    }

    /// <summary>Disable AutoApprove and persist.</summary>
    public static void DisableAutoApprove(AppConfig cfg)
    {
        cfg.AutoApprove = false;
        ConfigStore.Save(cfg);
        _sessionAllowAll = false;
    }
}
