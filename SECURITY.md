# Security

Argus is local-first. Conversations, graph data, settings, and memories are
stored in `%LOCALAPPDATA%\Argus`. Provider credentials are stored through
Windows Credential Locker rather than in the SQLite database or repository.

## Reporting a vulnerability

Please use GitHub's private security advisory flow for this repository. Do not
open a public issue for an unpatched vulnerability or include real API keys,
Telegram tokens, database files, logs, or personal project paths in a report.

## Public issue hygiene

Before attaching diagnostics, remove:

- API keys, bot tokens, cookies, and authorization headers
- Telegram user and chat IDs
- local usernames and absolute filesystem paths
- conversation text, memories, project names, and Git remotes
- `%LOCALAPPDATA%\Argus\argus.db` and `soul.md`
