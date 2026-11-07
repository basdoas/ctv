
using System.Text.Json.Serialization;

namespace Ctv;

/// <summary>A single extension command definition.</summary>
public sealed class ExtensionCommand
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Shell command to run when this extension command is invoked.</summary>
    public string ShellCommand { get; set; } = "";
    /// <summary>If true, the command output is sent to the AI as context.</summary>
    public string? SendToAi { get; set; }
}

/// <summary>An extension manifest loaded from ~/.config/ctv/extensions/*.json</summary>
public sealed class ExtensionManifest
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public List<ExtensionCommand> Commands { get; set; } = new();
}
