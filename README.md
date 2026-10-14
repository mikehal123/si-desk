# SI Desk

SI Desk (SI = super intelligence) is an unofficial, community-built Windows desktop app for Grok.

**SI Desk is an independent project. It is not affiliated with or endorsed by SpaceXSI. Grok is their trademark.**
It is not affiliated with, endorsed by, or supported by xAI. xAI is a trademark of its owner.

## Quick start

Windows 10 or 11, 64-bit. Full steps, screens, and troubleshooting: **[Getting started](docs/GETTING-STARTED.md)**.

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

![Swarm tab](docs/screenshots/swarm.png)
*Swarm: a goal split into engineer and verifier tasks, all done.*

![Subagents tab](docs/screenshots/subagents.png)
*Subagents: helper cards with model, status and run time.*

![Changes review](docs/screenshots/changes.png)
*Changes: line-by-line review of an agent's file edits before anything is applied.*

![Grokbot tab](docs/screenshots/grokbot.png)
*Grok Bot: an assistant that reads your chats and can act inside SI Desk when you allow it.*

## Requirements

- Windows 10 or 11 (x64)
- One connection: a Cursor subscription, the Grok Build CLI, or an API key for the xAI API
- Cursor users click **Install Cursor** in the chat **Connection** panel, or run `setup\Install-CursorConnection.ps1` from the unzipped release once (the same file is `scripts\Install-CursorConnection.ps1` in a source checkout). See [Getting started](docs/GETTING-STARTED.md).
- Shared room expects `room-tunnel\cloudflared.exe` beside the app and checks a pinned SHA-256. This repository does not include that file.
- The .NET SDK is only needed to build from source

## Build

From the repo root:

```
dotnet build apps/desktop/Sovereign.Desktop -c Debug -p:Platform=x64
```

This is the expected build command. Please check it against the project files before you rely on it.

## Shared room setup

Shared room invites need cloudflared, which is not included in this repo. The app checks the copy it runs and verifies its SHA256. Tunnel state is kept in the SI Desk profile at `%LOCALAPPDATA%\SIDesk`.

## Status

Public beta. Expect rough edges, and bug reports are welcome.

## Credits

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Author

Built by Michael Louvris. Find me on X: [@grokbuildAI](https://x.com/grokbuildAI)

## License

MIT. See [LICENSE](LICENSE).
