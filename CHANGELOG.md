# Changelog

All notable changes to Argus are documented here.

## 0.1.5 - 2026-06-06

- Made the Settings API Key field conditionally visible, hiding it for trusted local endpoints (localhost, 127.0.0.1, ::1).
- Added native support for Anthropic API request/response mapping and model catalog.
- Added dashboard widget customization. Toggling widgets hides/shows them dynamically and collapses their corresponding grid columns, persistently saving preferences.

## 0.1.4 - 2026-06-06

- Added predefined LM Studio integration profile matching default port 1234.
- Fixed a model list loading bug where OpenAI-compatible local/custom models (LM Studio, Ollama, Hugging Face, etc.) were filtered out and not shown due to prefix matching.
- Updated documentation with instructions for local model setup.

## 0.1.3 - 2026-06-06

- Embedded the custom application icon into the executable, fixing the issue where the application and its shortcuts had default Windows icons.

## 0.1.2 - 2026-06-06

- Added connection management buttons (Save LLM Settings, Test Connection, Refresh Models) for AI Provider API Key configuration.
- Added visual indicator showing if API keys and Telegram bot tokens are saved or configured.
- Fixed an issue where API keys entered in Settings were not stored persistently and did not survive app restarts.

## 0.1.1 - 2026-06-06

- Added a new wolf-seal application identity across the executable, title bar, sidebar, Windows tiles, and splash assets.
- Reframed the public documentation around Argus as a supervised AI agent with durable local memory, connected graph context, project awareness, tools, and LLM support.
- Replaced the README gallery with fullscreen application screenshots.
- Added SHA-256 verification before the in-app updater launches a downloaded installer.
- Removed the last user-specific projects-directory example from the command palette.

## 0.1.0 - 2026-06-06

- Initial public release.
- Unified desktop workspace for AI chat, web research, projects, knowledge, graphs, system monitoring, and Telegram access.
- Local SearXNG integration with raw-result fallback when no model provider is configured.
- Secure provider and Telegram credential storage through Windows Credential Locker.
- Self-contained Windows installer and portable ZIP package.
- GitHub Releases update checks and in-app install flow.
