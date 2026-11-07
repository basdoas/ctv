using System.Text;
using Spectre.Console;

namespace Ctv;

public sealed record Theme(string Name, (int R, int G, int B) From, (int R, int G, int B) To)
{
    public string AccentHex => Hex(From);
    public static string Hex((int R, int G, int B) c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
}

public static class Ui
{
    public static readonly Theme[] Themes =
    {
        new("aurora", (0, 245, 160),   (123, 97, 255)),
        new("sunset", (255, 81, 47),   (255, 200, 40)),
        new("ocean",  (0, 198, 255),   (0, 114, 255)),
        new("neon",   (255, 0, 204),   (51, 51, 255)),
        new("mono",   (255, 255, 255), (130, 130, 130)),
    };

    public static Theme Get(AppConfig cfg) =>
        Themes.FirstOrDefault(t => t.Name == cfg.Theme) ?? Themes[0];

    /// <summary>Arrow-key menu; falls back to a numbered list if the terminal can't do it.</summary>
    public static string Choose(string title, IEnumerable<string> choices)
    {
        var list = choices.ToList();
        try
        {
            return AnsiConsole.Prompt(new SelectionPrompt<string>().Title(title).AddChoices(list));
        }
        catch (NotSupportedException)
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(title)}[/]");
            for (int i = 0; i < list.Count; i++)
                Console.WriteLine($"  {i + 1}) {list[i]}");
            while (true)
            {
                Console.Write("number: ");
                var s = Console.ReadLine() ?? throw new OperationCanceledException();
                if (int.TryParse(s.Trim(), out var n) && n >= 1 && n <= list.Count)
                    return list[n - 1];
            }
        }
    }

    static readonly string[] Logo =
    {
        " ██████  ████████   ██    ██        ",
        "██          ██      ██    ██        ",
        "██          ██      ██    ██        ",
        "██          ██       ██  ██         ",
        " ██████ool  ██erminal ████ibe client",
    };

    public static void Banner(AppConfig cfg)
    {
        var t = Get(cfg);
        int w = Logo.Max(l => l.Length);
        var sb = new StringBuilder();
        foreach (var line in Logo)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ') { sb.Append(' '); continue; }
                double f = (double)i / (w - 1);
                var c = (
                    R: (int)(t.From.R + (t.To.R - t.From.R) * f),
                    G: (int)(t.From.G + (t.To.G - t.From.G) * f),
                    B: (int)(t.From.B + (t.To.B - t.From.B) * f));
                sb.Append($"[{Theme.Hex(c)}]{line[i]}[/]");
            }
            sb.AppendLine();
        }
        AnsiConsole.Markup(sb.ToString());
        AnsiConsole.MarkupLine("[dim]cool terminal vibe client · type /help for commands[/]");
        AnsiConsole.Write(new Rule().RuleStyle(Style.Parse(t.AccentHex)));
    }
}
