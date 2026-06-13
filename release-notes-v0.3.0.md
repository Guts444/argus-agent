Argus v0.3.0 — Daily Use and Product Polish

The first-run experience is now a guided setup wizard, the dashboard has
real project intelligence, and conversation search makes past context
instantly retrievable.

**New in v0.3.0**

- **First-run setup wizard** — three-step onboarding on fresh databases:
  pick an LLM provider and enter credentials, choose a projects folder,
  review privacy defaults. Skips automatically on subsequent launches.

- **Project cockpit** — the dashboard's Active Projects widget is now a
  live intelligence view. Each project card shows repo health (uncommitted
  changes, Git branch), open task count, recorded decisions, and active
  blockers. Global blocker alerts and cross-project next-action guidance
  are surfaced at the top of the widget.

- **Conversation search** — full-text search across all messages with
  FTS5 indexing. Results show role badges, conversation titles,
  timestamps, and `<mark>`-highlighted snippets.

- **Smart widget defaults** — Market, Sports, and Projects widgets are
  hidden on first launch until the user configures stock symbols, a
  league, or a projects root path. System Status and Intel Feed remain
  always-visible.

- **Per-source context token breakdown** — the CTX status-bar flyout
  now shows estimated token counts per context layer: Soul, Memories,
  Project, and Projects Index.

- **Empty-state placeholders** — Conversations, Memory Debugger, and
  Graph Search views show helpful guidance text when empty instead of
  blank panels.

**Under the hood**

- 54 regression tests, all passing
- Version bumped to 0.3.0 across csproj, Codex client info, updater
  fallback, and README
- New converters: CountToVisibility, StringToVisibility,
  WarningBackground, WarningForeground
- Architecture documentation cleaned up — v0.2 deferred list removed,
  v0.4 roadmap clarified

**Install**

```
irm https://raw.githubusercontent.com/Guts444/argus-agent/main/scripts/install.ps1 | iex
```

Or download `ArgusAgentSetup-x64.exe` from this release. SHA-256
checksum is published alongside the installer.

**Upgrading from v0.2.0**

The first launch after upgrading will skip the setup wizard (the
SetupCompleted marker is preserved). Your existing providers, settings,
and database are unchanged.
