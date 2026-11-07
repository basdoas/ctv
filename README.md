# ctv
cool terminal vibe client

A terminal chat client written in C# with gradient themes and in-app configuration.

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
| `/clear` | clear conversation |
| `/quit` | exit (Ctrl+C stops a running answer) |

## Providers

OpenAI, Anthropic (Claude), Google Antigravity, NVIDIA NIM, Mistral, OpenRouter, Groq, Ollama, or any custom OpenAI-compatible endpoint.
API keys can be stored in `~/.config/ctv/config.json` (file mode 600) or referenced as `env:VAR_NAME`.

Adding a provider = one class implementing `IChatProvider` in `Providers.cs`.
