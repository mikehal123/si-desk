# xAI Desk

xAI Desk is an unofficial, community-built Windows desktop app for Grok.
It is **not affiliated with, endorsed by, or supported by xAI.**

## Features

- **Chat** with File > Open Folder, so Grok can work on your code.
- **Three ways to connect:**
  - your Cursor subscription, using Grok models
  - your Grok Build CLI subscription
  - a paid xAI API key
- **Subagents** that help with parts of a task.
- **Swarm mode.** A manager splits a goal into parallel engineer, researcher and verifier tasks. Nothing counts as done until a verifier proves it. If a worker fails, you can press Respawn to retry it once.
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
*Grokbot: an assistant that reads your chats and can act inside xAI Desk when you allow it.*

## Requirements

- Windows 10 or 11 (x64)
- .NET 10 SDK
- At least one connection: a Cursor subscription, a Grok Build CLI subscription, or an xAI API key

## Build

From the repo root:

```
dotnet build apps/desktop/Sovereign.Desktop -c Debug -p:Platform=x64
```

This is the expected build command. Please check it against the project files before you rely on it.

## Shared room setup

Shared room invites need cloudflared, which is not included in this repo.

1. Download the Windows amd64 build of cloudflared from https://github.com/cloudflare/cloudflared/releases
2. Rename it `cloudflared.exe` if needed, and put it in `apps/desktop/room-tunnel/`.

## Status

Public beta. Expect rough edges, and bug reports are welcome.

## Credits

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Author

Built by a self-taught longshoreman from BC. Find me on X: [@grokbuildAI](https://x.com/grokbuildAI)

## License

MIT. See [LICENSE](LICENSE).
