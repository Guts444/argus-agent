<div align="center">

# Argus

**A Windows-native, local-first AI command center for your projects, memory, research, and automations.**

[![CI](https://github.com/Guts444/argus-agent/actions/workflows/ci.yml/badge.svg)](https://github.com/Guts444/argus-agent/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Guts444/argus-agent)](https://github.com/Guts444/argus-agent/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-cyan.svg)](LICENSE)

[Install](#install) · [Features](#features) · [SearXNG](#private-web-search-with-searxng) · [Telegram](#telegram-gateway) · [Development](#development)

</div>

![Argus command center showing a live AI news search](docs/images/argus-dashboard-search.png)

Argus combines an interactive knowledge graph, persistent conversations,
local memory, supervised AI tools, live dashboard feeds, project awareness,
and Telegram access in one native WinUI 3 desktop app. Your graph and chat
history stay in a local SQLite database, while model credentials are protected
by Windows Credential Locker.

## Install

### PowerShell

Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/Guts444/argus-agent/main/scripts/install.ps1 | iex
```

The script downloads the latest `ArgusAgentSetup-x64.exe` from GitHub Releases.
The installer contains the .NET and Windows App SDK runtime files Argus needs.

### Manual download

Download `ArgusAgentSetup-x64.exe` from the
[latest release](https://github.com/Guts444/argus-agent/releases/latest).
SHA-256 checksums are published beside each release.

**Requirements:** Windows 10 version 1809 or newer, x64. Windows 11 is
recommended. Docker Desktop is optional and only needed for local web search.

> The community installer is currently not Authenticode-signed. Windows may
> show a SmartScreen warning. Verify the release checksum before installing.

## Features

### Local knowledge and memory

- Animated Win2D graph with pan, zoom, drag, selection, minimap, type filters,
  clustering, fit/reset controls, and direct node creation.
- Create, edit, connect, archive, delete, tag, import, and export graph data.
- SQLite persistence with EF Core migrations and FTS5 search for nodes and
  conversation messages.
- Local memory recall automatically adds relevant context to conversations.
- Messages can become durable memories and graph nodes.

### Supervised AI agent

- Agent tools for graph search, memory search, node and edge changes, memory
  capture, and SearXNG web research.
- Tool execution is bounded and its reasoning log is kept separate from the
  user-facing answer.
- Collapsible thinking and web-search activity, including visited websites.
- `/new` starts a clean conversation and `/web <query>` runs explicit research.

### Models and reasoning

- DeepSeek, OpenAI, OpenRouter, local models, and custom OpenAI-compatible
  endpoints.
- Provider and model selection from the bottom status bar.
- Optional thinking mode and configurable reasoning effort.
- Live model catalog refresh for OpenAI and OpenRouter.
- Context usage tracking from provider-reported tokens with a local fallback.
- Localhost model endpoints can run without an API key.

### Command center

- Live CPU, memory, network, disk, GPU, process, and uptime metrics.
- Configurable market quotes through Yahoo Finance.
- AI, technology, and gaming feeds aggregated from public RSS sources.
- Configurable soccer scores through ESPN's public scoreboard endpoint.
- Active projects, recent thoughts, persistent chat, and a command palette.

### Projects and remote access

- Optional local project scanning with README previews, Git remotes, branches,
  and working-tree summaries.
- AI-assisted summaries for selected projects.
- Telegram gateway with polling or webhook mode, per-chat conversation
  isolation, allowlist enforcement, `/new`, `/undo`, and formatted replies.
- Editable `soul.md` persona for the Argus agent.

### Desktop release experience

- Self-contained Windows installer and Start menu shortcut.
- GitHub Releases based update checks.
- The bottom-right version control shows when a newer release is available and
  can download and launch the update installer.

## Knowledge Graph

![Argus knowledge graph and inspector](docs/images/argus-graph.png)

The graph is the durable center of Argus. Projects, ideas, tasks, decisions,
memories, tools, and agents can be connected with typed relationships. FTS5
search, tags, project context, JSON import/export, and the inspector make it
useful beyond visualization.

## Private Web Search with SearXNG

Argus uses a local [SearXNG](https://docs.searxng.org/) instance for web
research. It does not silently proxy searches through a hosted Argus service.

1. Install and start Docker Desktop.
2. Clone this repository.
3. Start the included local-only SearXNG configuration:

```powershell
git clone https://github.com/Guts444/argus-agent.git
cd argus-agent
docker compose -f docker-compose.searxng.yml up -d
```

The service binds to `127.0.0.1:8080`. Verify it with:

```powershell
irm "http://127.0.0.1:8080/search?q=latest%20AI%20news&format=json"
```

Then use `/web latest AI news` in Argus, or enable **Auto Web Search** under
**Skills**. Stop the service with:

```powershell
docker compose -f docker-compose.searxng.yml down
```

## Model Setup

Configure a provider in **Settings**, then choose the active provider, model,
thinking mode, and reasoning effort from the status bar.

Provider keys are stored in Windows Credential Locker. For development and
automated launches, Argus also recognizes:

| Provider | Environment variables |
| --- | --- |
| DeepSeek | `DEEPSEEK_API_KEY`, `ARGUS_DEEPSEEK_API_KEY` |
| OpenAI | `OPENAI_API_KEY`, `ARGUS_OPENAI_API_KEY` |
| OpenRouter | `OPENROUTER_API_KEY`, `ARGUS_OPENROUTER_API_KEY` |

OpenAI routing can additionally use `OPENAI_ORG_ID`,
`OPENAI_ORGANIZATION`, `OPENAI_PROJECT_ID`, or `OPENAI_PROJECT`.

For Ollama, LM Studio, vLLM, and other compatible servers, add a custom
OpenAI-compatible profile such as `http://localhost:11434/v1`.

## Telegram Gateway

![Argus project and Telegram gateway settings](docs/images/argus-settings.png)

1. Create a bot with Telegram's `@BotFather`.
2. Open **Settings > Telegram Gateway**.
3. Enter the bot token and an explicit comma-separated user allowlist.
4. Choose polling or webhook mode.
5. Save and enable the gateway.

The token is stored in Windows Credential Locker. An empty allowlist blocks all
Telegram chats. Polling is the simplest local setup; webhook mode requires a
public HTTPS endpoint that forwards to the configured local listener.

## Local Data and Security

Argus stores application data under:

```text
%LOCALAPPDATA%\Argus\
├── argus.db
└── soul.md
```

- Provider keys and the Telegram bot token use Windows Credential Locker.
- Project scanning is disabled until you choose a directory.
- SearXNG is bound to localhost by the included Docker configuration.
- No database, credential, local project path, or user-specific configuration
  is included in this repository.

See [SECURITY.md](SECURITY.md) before sharing logs or opening a security report.

## Keyboard and Chat Commands

| Command | Action |
| --- | --- |
| `Ctrl+K` | Open the command palette |
| `Enter` | Send the current chat message |
| `/new` | Start a new local conversation |
| `/web <query>` | Search through local SearXNG |

## Development

### Requirements

- Windows 10 version 1809 or newer
- .NET 10 SDK
- Visual Studio with Windows application development tools, recommended
- Inno Setup 6, only for building the installer

### Build and test

```powershell
git clone https://github.com/Guts444/argus-agent.git
cd argus-agent
dotnet restore
dotnet build Argus.slnx
dotnet test Argus.slnx
dotnet run --project Argus.App\Argus.App.csproj
```

### Build release artifacts

```powershell
winget install --id JRSoftware.InnoSetup --exact
.\scripts\build-release.ps1 -Version 0.1.0
```

Artifacts are written to `artifacts\installer`:

- `ArgusAgentSetup-x64.exe`
- `ArgusAgent-win-x64.zip`
- `SHA256SUMS.txt`

Tagged releases are also built by
[the release workflow](.github/workflows/release.yml).

## Architecture

```text
Argus.App    WinUI 3 UI, MVVM view models, Credential Locker, updater
Argus.AI     providers, agent loop, tools, feeds, Telegram gateway
Argus.Core   domain models and service contracts
Argus.Data   EF Core, SQLite, FTS5, migrations, graph and memory services
Argus.Tests  unit and integration coverage
```

## Status

Argus `v0.1.0` is an early public release. Core graph, memory, chat, project,
dashboard, web-search, provider, Telegram, installer, and update flows are
implemented. Image, voice, and file-analysis skills are not yet included.

## License

[MIT](LICENSE)
