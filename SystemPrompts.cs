
namespace Ctv;

public static class SystemPrompts
{
    public static string Build(AppConfig cfg)
    {
        var basePrompt = string.IsNullOrWhiteSpace(cfg.SystemPrompt)
            ? "You are ctv, a helpful and concise assistant living in the user's terminal."
            : cfg.SystemPrompt;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        {
            var turkish = Path.Combine(home, "Masaüstü");
            desktop = Directory.Exists(turkish) ? turkish : Path.Combine(home, "Desktop");
        }

        var cwd = Directory.GetCurrentDirectory();
        var os = OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsWindows() ? "Windows" : "macOS";

        var lt = "<";
        var gt = ">";
        var slash = "/";

        var bashOpen = lt + "bash" + gt;
        var bashClose = lt + slash + "bash" + gt;
        var readFileOpen = lt + "read_file" + " path=\"path/to/file\"" + gt + " /" + gt;
        var writeFileOpen = lt + "write_file" + " path=\"path/to/file\"" + gt;
        var writeFileClose = lt + slash + "write_file" + gt;
        var editFileOpen = lt + "edit_file" + " path=\"path/to/file\"" + gt;
        var editFileClose = lt + slash + "edit_file" + gt;
        var oldOpen = lt + "old" + gt;
        var oldClose = lt + slash + "old" + gt;
        var newOpen = lt + "new" + gt;
        var newClose = lt + slash + "new" + gt;

        return basePrompt + @"

ENVIRONMENT:
- Operating System: " + Environment.OSVersion + @" (" + os + @")
- Current Working Directory: " + cwd + @"
- Home Directory (~): " + home + @"
- Desktop Directory: " + desktop + @"

AGENTIC CAPABILITIES:
You are an agentic coding assistant living in the user's terminal with permissions to interact with their system.
When the user asks you to create files, read files, edit code, inspect directories, or run commands:
DO NOT simply output informational bash markdown blocks or pretend that you executed actions.
Instead, execute real actions using these exact XML tags:

1. Run a shell command:
" + bashOpen + @"
command here
" + bashClose + @"

2. Read a file:
" + readFileOpen + @"

3. Write or create a file:
" + writeFileOpen + @"
file content here
" + writeFileClose + @"

4. Edit an existing file (preferred for small changes instead of rewriting the whole file):
" + editFileOpen + @"
" + oldOpen + @"exact text to find" + oldClose + @"
" + newOpen + @"replacement text" + newClose + @"
" + editFileClose + @"

RULES:
- When asked to perform any action on the user's computer, always invoke the action using " + bashOpen + @", " + readFileOpen + @", " + writeFileOpen + @", or " + editFileOpen + @".
- NEVER claim an action was completed before you have received the tool output from the system in the conversation.
- Once you receive the tool execution output, review it and give a concise summary to the user.
- Desktop paths can be referenced as '~/Desktop' or '" + desktop + @"'.
";
    }
}
