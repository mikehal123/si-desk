# Changelog

## 0.3.68

- In-app updates. On startup, and from Check for updates in Settings, SI Desk makes one unauthenticated request to the latest public GitHub release. A newer non-pre-release shows "SI Desk X.Y.Z available" with Update and Later. Update downloads the release zip to `%TEMP%`, refuses it unless its SHA-256 matches GitHub's digest, and hands off to `SIDesk.UpdateHelper.exe`. The helper waits for SI Desk to exit, swaps only the app folder, keeps the old one as `<folder>.bak` until the new version starts, and rolls back if it does not. Profile, chats, settings and Credential Manager entries are not touched. A copy run from a source tree, a folder under a `.git` repo, or a folder with a `SIDesk.dev-build` file shows "dev build, updates off".
- Grok Bot connector: the audit log names why each request was refused (`missing-header`, `not-bearer`, `empty-token`, `wrong-token`, `no-token-set`), never the header or token. An inbox image is sent once; later lists send a reference line unless `include_images` is true. Listing no longer rewrites the mailbox when nothing changed, and the last poll time is saved at most once a minute.
- The saved Grok Bot token is kept across restarts, previous xAIDesk secrets are copied once, and a busy connector port is retried every 5 seconds.

## 0.3.67

- The sidebar header next to the SI logo and the Settings About card now read Super Intelligence Desk. The app name, window title, and SIDesk.exe are unchanged. The sidebar label wraps to two lines when the sidebar is narrow.

## 0.3.66

- The product name is SI Desk (SI = super intelligence). The program is SIDesk.exe, and setup installs to `%LOCALAPPDATA%\Programs\SI Desk`.
- On startup, if `%LOCALAPPDATA%\SIDesk` is missing and `%LOCALAPPDATA%\xAIDesk` exists, the old profile is copied and left in place so chats and settings carry over. Credential Manager reads the previous key names when the new names are empty.
- The Grok Bot tab stays Grok Bot.
- The window, installer, and phone icons use the SI mark: a navy rounded square with transparent corners.

## 0.3.65

- App and phone icons no longer use the SpaceXSI / xAI mark. They use this project's own artwork. Third-party notices now include PdfPig and the Windows App SDK.

## 0.3.64

- Single model mode uses the selected model for every swarm role. A missing model is an error instead of a silent fallback to another provider.
- Connect, refresh, and Grok Build silence errors name what is missing (xAI API key, Cursor sign-in, Grok CLI on PATH, auth, or network) and the next step.
- First launch and Settings include a setup check for the xAI API key, Cursor login, and Grok Build. Each line is OK or Missing. Secret values are not shown.

## 0.3.63

- Classic chat view, with rounded bubbles and cleaner panels, is now on by default.

## 0.3.62

- Easy install (`bfc6992`): the .NET 10 SDK rolls forward within the 10.0 band, `scripts/publish-release.ps1` builds a self-contained win-x64 zip, and setup does a real per-user install.
- Public cleanup (`4bcc8df`): private modules are out of the tree, the product name is xAI Desk, and the public scan allowlist matches that tree.
- Contributing guide, GitHub issue forms, and a `v*` release workflow that checks `Directory.Build.props` before publishing (`#2`).

## 0.3.61

- Grokbot tab uses the avatar mark, an unread badge, a thinking bubble, and Copy and Like on replies.
- Cursor chats can hire swarm workers as a single model or mixed models, with role rankings and a hire catalog on the prompt.
- Shared rooms download the pinned Windows tunnel tool when it is missing, check its checksum, and show Retry if that fails.
