
namespace Ctv;

/// <summary>
/// Full-screen TUI controller for ctv inspired by mistral-vibe.
/// Manages alternate screen buffer, header rendering, and terminal restoration.
/// </summary>
public sealed class FullScreenUi : IDisposable
{
    private readonly AppConfig _cfg;
    private bool _disposed;

    public FullScreenUi(AppConfig cfg)
    {
        _cfg = cfg;
    }

    public void Start()
    {
        VibeScreen.EnterAltScreen();
        VibeScreen.RenderHeader(_cfg);
    }

    public void Refresh()
    {
        VibeScreen.RenderHeader(_cfg);
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        VibeScreen.ExitAltScreen();
    }

    public void Dispose() => Stop();
}
