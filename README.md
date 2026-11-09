# ctv

. ██████  ████████   ██    ██        .
.██          ██      ██    ██        .
.██          ██      ██    ██        .
.██          ██       ██  ██         .
. ██████ool  ██erminal ████ibe client.


A terminal chat client written in mostly C# with gradient themes and in-app configuration.

## Install

### Any Linux (x86_64)

```bash
curl -fsSL https://raw.githubusercontent.com/basdoas/ctv/main/install.sh | bash
```

Downloads the latest release, checks its SHA-256 and installs it to `~/.local/bin` (or `/usr/local/bin` when run as root).
Options: `--version 0.2.0`, `--prefix /usr/local`, `--from-source` (needs the .NET SDK and clang), `--uninstall` (add `--purge` to also delete `~/.config/ctv`).

### Fedora (dnf)

```bash
# Fedora 41+ (dnf5)
sudo dnf config-manager addrepo --from-repofile=https://basdoas.github.io/ctv/ctv.repo

# Fedora 40 and older (dnf4: sudo dnf install dnf-plugins-core first)
sudo dnf config-manager --add-repo https://basdoas.github.io/ctv/ctv.repo

sudo dnf install ctv
```

Updates arrive with `sudo dnf upgrade`.

### Releases (maintainers)

Push a tag such as `v0.1.0`. `.github/workflows/release.yml` builds the native binary, then publishes the tarball + `SHA256SUMS`, the RPM and the dnf repository.

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
