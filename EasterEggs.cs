
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Spectre.Console;

namespace Ctv;

/// <summary>
/// Easter eggs and fun hidden commands for ctv.
/// </summary>
public static class EasterEggs
{
    private static readonly Random _rng = new();

    public static readonly List<string> EightBallAnswers = new()
    {
        "It is certain.",
        "It is decidedly so.",
        "Without a doubt.",
        "Yes definitely.",
        "You may rely on it.",
        "As I see it, yes.",
        "Most likely.",
        "Outlook good.",
        "Yes.",
        "Signs point to yes.",
        "Reply hazy, try again.",
        "Ask again later.",
        "Better not tell you now.",
        "Cannot predict now.",
        "Concentrate and ask again.",
        "Don't count on it.",
        "My reply is no.",
        "My sources say no.",
        "Outlook not so good.",
        "Very doubtful.",
    };

    /// <summary>Handle an easter egg command. Returns true if handled.</summary>
    public static bool TryHandle(string line, out string? output)
    {
        output = null;
        var cmd = line.Trim().ToLowerInvariant();

        switch (cmd)
        {
            case "/vibe":
                output = RenderVibeLogo();
                return true;

            case "/easter":
            case "/eggs":
                output = RenderEasterList();
                return true;

            case "/coin":
            case "/flip":
                output = _rng.Next(2) == 0 ? "[green]Heads![/]" : "[red]Tails![/]";
                return true;

            case "/dice":
            case "/roll":
                output = $"[bold]🎲 {_rng.Next(1, 7)}[/]";
                return true;

            case "/8ball":
            case "/eightball":
                output = $"[bold]{EightBallAnswers[_rng.Next(EightBallAnswers.Count)]}[/]";
                return true;

            case "/matrix":
                output = RenderMatrix();
                return true;

            case "/hello there":
                output = "[yellow]General Kenobi![/]";
                return true;

            case "/who are you":
            case "/what are you":
                output = "[bold]I'm ctv[/], your cool terminal vibe client. 😎";
                return true;

            case "/rickroll":
                output = "[dim]Never gonna give you up, never gonna let you down...[/] 🎵";
                return true;

            case "/42":
            case "/answer":
                output = "[bold]42[/] — but what was the question?";
                return true;

            case "/cool":
                output = "[bold cyan]😎[/] You're already cool.";
                return true;

            default:
                return false;
        }
    }

    private static string RenderVibeLogo()
    {
        var lines = new[]
        {
            " ██████  ████████   ██    ██        ",
            "██          ██      ██    ██        ",
            "██          ██      ██    ██        ",
            "██          ██       ██  ██         ",
            " ██████ool  ██erminal ████ibe client",
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        foreach (var line in lines)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ') { sb.Append(' '); continue; }
                double f = (double)i / (line.Length - 1);
                var c = (
                    R: (int)(0 + (123 - 0) * f),
                    G: (int)(245 - (245 - 97) * f),
                    B: (int)(160 + (255 - 160) * f));
                sb.Append($"[#{c.R:x2}{c.G:x2}{c.B:x2}]{line[i]}[/]");
            }
            sb.AppendLine();
        }
        sb.AppendLine("[dim]cool terminal vibe client · type /easter for surprises[/]");
        return sb.ToString();
    }

    private static string RenderEasterList()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("[bold]Easter eggs:[/]");
        sb.AppendLine("  [bold]/vibe[/]       — show the ctv logo");
        sb.AppendLine("  [bold]/coin[/]       — flip a coin");
        sb.AppendLine("  [bold]/dice[/]       — roll a die");
        sb.AppendLine("  [bold]/8ball[/]      — magic 8-ball");
        sb.AppendLine("  [bold]/matrix[/]     — brief matrix rain");
        sb.AppendLine("  [bold]/hello there[/] — you know the rest");
        sb.AppendLine("  [bold]/who are you[/]  — ctv introduction");
        sb.AppendLine("  [bold]/rickroll[/]    — never gonna give you up");
        sb.AppendLine("  [bold]/42[/]         — the answer to everything");
        sb.AppendLine("  [bold]/cool[/]       — confirm your coolness");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string RenderMatrix()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        var chars = "アイウエオカキクケコサシスセソタチツテトナニヌネノ0123456789";
        for (int row = 0; row < 6; row++)
        {
            for (int col = 0; col < 60; col++)
            {
                if (_rng.Next(10) == 0)
                    sb.Append($"[green]{chars[_rng.Next(chars.Length)]}[/]");
                else if (_rng.Next(20) == 0)
                    sb.Append("[dim green]#[/]");
                else
                    sb.Append(' ');
            }
            sb.AppendLine();
        }
        sb.AppendLine("[dim]...[/]");
        return sb.ToString();
    }
}
