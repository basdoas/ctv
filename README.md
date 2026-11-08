# ctv

   ██████  ████████   ██    ██        
  ██          ██      ██    ██        
  ██          ██      ██    ██        
  ██          ██       ██  ██         
   ██████ool  ██erminal ████ibe client


A terminal chat client written in mostly C# with gradient themes and in-app configuration.

## Run

```bash
dotnet run                                  # JIT, fast iteration
dotnet publish -c Release -r linux-x64      # native AOT binary (needs clang + zlib1g-dev)
```

On first start ctv walks you through adding a provider. Everything can be changed later inside the app.

## Commands

| command | what it does |
|---|---|
| `/login` | add or update a provider |
| `/provider` | switch active provider |
| `/model [name]` | change model |
| `/theme` | pick a theme (aurora, sunset, ocean, neon, mono) |
| `/system` | edit the system prompt |
| `/config` | show config file + providers |
| `/permissions` | view and manage safety allow-list & auto-approve |
| `/clear` | clear conversation & session permissions |
| `/quit` | exit (Ctrl+C stops a running answer) |

## File & Terminal Access (Agentic)

- **File Attachments**: Reference any file using `@path`, e.g.:
  ```
  @./src/Program.cs can you optimize this loop?
  ```
- **Shell Commands**: Execute local terminal commands with `!command`, optionally followed by two spaces and a question:
  ```
  !git status
  !dotnet test  explain the failing test
  ```

## Safety & Permissions

Every file read and shell execution is protected by a safety gate:
1. **Allow once**: Run the action this one time.
2. **Allow in this session**: Trust the action until ctv exits or `/clear`.
3. **Always allow this action**: Persist this exact command or action to `~/.config/ctv/config.json`.
4. **Always allow everything (auto-mode)**: Auto-approve all future actions.

Use `/permissions` to inspect, edit, or reset your allow-list at any time.

## Providers

OpenAI, Anthropic (Claude), Google Antigravity, NVIDIA NIM, Mistral, OpenRouter, Groq, Ollama, or any custom OpenAI-compatible endpoint.
API keys can be stored in `~/.config/ctv/config.json` (file mode 600) or referenced as `env:VAR_NAME`.

Adding a provider = one class implementing `IChatProvider` in `Providers.cs`.
