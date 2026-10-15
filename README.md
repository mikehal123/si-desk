# SI Desk

SI Desk (SI = super intelligence) is an unofficial, community-built Windows desktop app for Grok.

**SI Desk is an independent project. It is not affiliated with or endorsed by SpaceXSI. Grok is their trademark.**
It is not affiliated with, endorsed by, or supported by xAI. xAI is a trademark of its owner.

**New here?** Follow **[How to use SI Desk](docs/HOW-TO-USE.md)**. It walks through install, connecting a provider, chat, subagents, swarm, Changes, Shared room, and Grok Bot.

## Quick start

Windows 10 or 11, 64-bit. Screen-by-screen steps: **[How to use SI Desk](docs/HOW-TO-USE.md)**. Installer notes and the full status-line list: **[Getting started](docs/GETTING-STARTED.md)**.

1. Download [`SIDesk-0.3.66-win-x64.zip`](https://github.com/mikehal123/si-desk/releases) from the latest release, unzip it, and run `SIDesk.exe`. The build is unsigned. If Windows SmartScreen warns, click **More info**, then **Run anyway**.
2. A Cursor subscription needs a one-time connector install. The app does not find `cursor` or `cursor-agent` on PATH. Open **Connection** and click **Install Cursor**, or from the unzipped folder run `powershell -NoProfile -ExecutionPolicy Bypass -File .\setup\Install-CursorConnection.ps1`. Then click **Sign in with Cursor** and **Connect / refresh models**.
3. Grok Build uses a `grok.exe` already on the PC. An xAI API key is saved in **Settings**. Either one is enough if you are not using Cursor.

Source and releases: https://github.com/mikehal123/si-desk

## Features

- **Chat** with File > Open Folder, so Grok can work on your code.
- **Three ways to connect:**
  - a Cursor subscription on this PC, which can select Grok models
  - the Grok Build CLI on this PC, using the subscription that CLI is signed in with
  - an API key sent to the xAI API
- **Subagents** that help with parts of a task.
- **Swarm mode.** A manager splits a goal into parallel engineer, fact-finding and verifier tasks. Nothing counts as done until a verifier proves it. If a worker fails, you can press Respawn to retry it once.
- **Changes review tab** to look over edits before you keep them.
- **Grok Bot connector** (optional) to add a main assistant.
- **Shared room** to build together. Send an invite and other people can join.

## Screenshots

<!-- SCREENSHOT: sidebar with Super Intelligence Desk label, Swarm tab, a goal split into engineer and verifier tasks, all done -->
*Swarm: a goal split into engineer and verifier tasks, all done.*

<!-- SCREENSHOT: sidebar with Super Intelligence Desk label, Subagents panel with helper cards (model, status, run time) and the Old section -->
*Subagents: helper cards with model, status and run time.*

<!-- SCREENSHOT: sidebar with Super Intelligence Desk label, Changes tab with a line-by-line review of an agent's file edits before anything is applied -->
*Changes: line-by-line review of an agent's file edits before anything is applied.*

<!-- SCREENSHOT: sidebar with Super Intelligence Desk label, Grok Bot mailbox with Ask Grokbot to act and Stop Grokbot -->
*Grok Bot: an assistant that reads your chats and can act inside SI Desk when you allow it.*

## Requirements

- Windows 10 or 11 (x64)
- One connection: a Cursor subscription, the Grok Build CLI, or an API key for the xAI API
- Cursor users click **Install Cursor** in the chat **Connection** panel, or run `setup\Install-CursorConnection.ps1` from the unzipped release once (the same file is `scripts\Install-CursorConnection.ps1` in a source checkout). See [Getting started](docs/GETTING-STARTED.md).
- Online shared rooms ship with pinned cloudflared 2026.9.1 (`room-tunnel\cloudflared.exe`, SHA-256 `2837888cc0f5d58f15b6dc478376de90b4d3ba5241c7947455d1e0a0df429712`). That exe is not committed; release builds download it and check the hash.
- The .NET SDK is only needed to build from source

## Build

From the repo root:

```
dotnet build apps/desktop/Sovereign.Desktop -c Debug -p:Platform=x64
```

This is the expected build command. Please check it against the project files before you rely on it.

## Shared room setup

Shared room invites use pinned cloudflared 2026.9.1. Release zips include `room-tunnel\cloudflared.exe`, and the app checks that file's SHA-256 (`2837888cc0f5d58f15b6dc478376de90b4d3ba5241c7947455d1e0a0df429712`) before opening the tunnel. The git checkout does not contain the exe. Tunnel state is kept in the SI Desk profile at `%LOCALAPPDATA%\SIDesk`.

## Status

Public beta. Expect rough edges, and bug reports are welcome.

## Credits

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Author

Built by Michael Louvris. Find me on X: [@grokbuildAI](https://x.com/grokbuildAI)

## License

MIT. See [LICENSE](LICENSE).
