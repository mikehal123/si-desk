# Changelog

## 0.3.64

- Single model mode uses the selected model for every swarm role. A missing model is an error instead of a silent fallback to another provider.
- Connect, refresh, and Grok Build silence errors name what is missing (xAI API key, Cursor sign-in, Grok CLI on PATH, auth, or network) and the next step.
- First launch and Settings include a setup check for the xAI API key, Cursor login, and Grok Build. Each line is OK or Missing. Secret values are not shown.

## 0.3.62

- Easy install (`bfc6992`): the .NET 10 SDK rolls forward within the 10.0 band, `scripts/publish-release.ps1` builds a self-contained win-x64 zip, and setup does a real per-user install.
- Public cleanup (`4bcc8df`): private modules are out of the tree, the product name is xAI Desk, and the public scan allowlist matches that tree.
- Contributing guide, GitHub issue forms, and a `v*` release workflow that checks `Directory.Build.props` before publishing (`#2`).

## 0.3.61

- Grokbot tab uses the avatar mark, an unread badge, a thinking bubble, and Copy and Like on replies.
- Cursor chats can hire swarm workers as a single model or mixed models, with role rankings and a hire catalog on the prompt.
- Shared rooms download the pinned Windows tunnel tool when it is missing, check its checksum, and show Retry if that fails.
