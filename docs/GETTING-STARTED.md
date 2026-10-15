# Getting started with SI Desk

SI Desk is an unofficial Windows desktop app in public beta. This guide is for someone trying the app for the first time. It describes what version **0.3.66** actually does.

SI Desk is an independent project. It is not affiliated with or endorsed by SpaceXSI. Grok is their trademark.

You need **Windows 10 or Windows 11, 64-bit**. The release zip already contains the app runtime, so you do not install .NET yourself.

You need one way to talk to a model:

- a **Cursor** subscription on this PC, or
- the **Grok Build** command-line program on this PC, or
- an **xAI API** key

Cursor is the connection that can edit a project folder, run subagents, and use swarm mode. Grok Build can do those too. The xAI API connection is chat only.

## Which connection should I pick?

| Connection | What it needs | What it can do |
| --- | --- | --- |
| Cursor subscription | A Cursor subscription on this PC, the connector install below, and Cursor sign-in | Chat. With a folder open: file edits, subagents, and swarm mode |
| Grok Build | `grok.exe` from xAI's Windows installer, then `grok login` with a grok.com account. Paid plans are in [Plans](#plans) | Chat. With a folder open: file edits, subagents, and swarm mode |
| xAI API key | A key from [console.x.ai](https://console.x.ai). Limits follow that team's API credits, not a grok.com plan. See [Plans](#plans) | Chat only |

## Download, unzip, and run

1. Open the latest release on GitHub: [github.com/mikehal123/si-desk/releases](https://github.com/mikehal123/si-desk/releases).
2. Download **`SIDesk-0.3.66-win-x64.zip`**.
3. Unzip the whole zip into a folder you can keep, such as a folder on your Desktop. Leave the files together. `setup\Install-CursorConnection.ps1` has to stay next to `SIDesk.exe`.
4. Double-click **`SIDesk.exe`**.

That zip is the app. You can run it from the unzipped folder.

The zip also contains `SIDesk.Setup.exe`. That program installs a copy under `%LOCALAPPDATA%\Programs\SI Desk` only when it is given a payload folder (`--payload`). The 0.3.66 zip does not include a `payload` folder. If setup prints **Payload directory was not found**, close it and start `SIDesk.exe` from the unzipped folder instead.

## Windows SmartScreen

The build is unsigned. Windows SmartScreen often says it protected your PC, or that the publisher is unknown. Click **More info**, then **Run anyway**.

You may see that warning the first time you open `SIDesk.exe`. It is the same warning for an unsigned app, not a message from SI Desk.

## First launch

The first time the profile folder is new, SI Desk opens a **Setup check** window. Each line is **OK** or **Missing**. The window does not show keys or tokens. **Close** dismisses it. **Check again** runs the same check.

The three lines are:

| Line | OK means | Missing means |
| --- | --- | --- |
| xAI API key | A saved key was accepted and the API listed a Grok model | No key, the key was rejected, the network blocked the check, or no Grok model was listed |
| Cursor login | The Cursor connector files are present and a Cursor CLI sign-in file is present | The connector is not installed, or it is installed and you still need to sign in |
| Grok Build | `grok.exe` was found | The Grok CLI was not found |

You only need one of these to be OK. The window appears once. After that, open **Settings** and click **Check setup** to see it again.

The chat screen opens with the provider set to **Cursor**. The status line says to connect, and to open a folder when you want tools and file edits.

## Connect a provider

On the chat screen, click **Connection** (top right). The panel title is **Connection**. The dropdown is the provider. The same panel has **Connect / refresh models**, sign-in, **Open Folder…**, **Close Folder**, and **New conversation**.

**Connect / refresh models** asks the provider for its model list and fills the model dropdown on the chat. It gives up after about 50 seconds. A successful connect closes the panel. Sending a message stays off until a live model list comes back.

### Cursor subscription

SI Desk does not talk to Cursor through an API key, and it does not look up `cursor` or `cursor-agent` on PATH. It starts a pinned copy of Cursor's agent package: `node.exe` plus `index.js`, version **2026.09.23-86fc751**. Your Cursor account stays with Cursor. Usage follows that account.

Do these steps in order.

#### 1. Install the Cursor connector

On the chat screen, open **Connection** and leave the provider on **Cursor**. If the pinned connector is missing, the panel shows **Install Cursor**. Click it. That runs `setup\Install-CursorConnection.ps1` beside `SIDesk.exe`. It does not read account keys and it does not run a remote install script. **Connect / refresh models** stays in the panel. When the status line says the connector is installed, click **Sign in with Cursor**, then **Connect / refresh models**.

If that script is not beside the app, **Install Cursor** opens the official Cursor CLI installation page, <https://cursor.com/docs/cli/installation>, instead. A Cursor IDE install, or a `cursor-agent` command on PATH, is still not this connector.

You can also install from the unzipped folder without the button.

1. Open the unzipped folder (the one that contains `SIDesk.exe`).
2. In that folder, open PowerShell and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup\Install-CursorConnection.ps1
```

If you are running from a source checkout instead of the zip, the same script is `scripts\Install-CursorConnection.ps1`.

What the script does:

- It downloads `https://downloads.cursor.com/lab/2026.09.23-86fc751/windows/x64/agent-cli-package.zip`.
- It checks the file's SHA-256 against the hash pinned in the script (`9c7232600ca77e7a6327dc9a0036277bd57b66923c6b65fc7abe9d957eb728e2`).
- It requires `node.exe` and `index.js` inside the package.
- It places that package at `%LOCALAPPDATA%\SovereignAI.Tools\CursorAgent\2026.09.23-86fc751\dist-package`.
- It does not change PATH, and it does not sign you in.
- If that version folder already exists, it stops and does not overwrite it.

When it finishes, it prints **Cursor connection installed. Open SI Desk, New chat, Cursor, then Sign in with Cursor. Uses your own Cursor account and limits. No PATH changes.** Continue with the steps below.

The app looks for `node.exe` and `index.js` in this order, and uses the first folder that has both files:

1. `%LOCALAPPDATA%\SIDesk\Tools\CursorAgent\2026.09.23-86fc751\dist-package`
2. `%LOCALAPPDATA%\xAIDesk\Tools\CursorAgent\2026.09.23-86fc751\dist-package` (a copy left from the older xAI Desk profile)
3. `%LOCALAPPDATA%\SovereignAI.Tools\CursorAgent\2026.09.23-86fc751\dist-package` (where the script installs it)

A Cursor IDE install, or a `cursor-agent` command that lives somewhere else, is not one of these folders. Connect will still say the connector is not installed until one of these three folders is complete.

#### 2. Sign in with Cursor

1. Start `SIDesk.exe`.
2. Click **Connection**.
3. Leave the provider on **Cursor**.
4. Click **Sign in with Cursor**.

The status line says **Complete Cursor sign-in in your browser. Waiting for Cursor…** SI Desk starts `node.exe index.js login` and waits up to 3 minutes. Finish the Cursor login in the browser. When the command exits successfully, the status line says **Cursor sign-in finished. Click Connect / refresh models.**

You can do the same sign-in from **Settings**, under **Connections**, on the **Cursor** row, with **Sign in**. That row's status is only **Connector installed** or **Connector not installed**. It does not by itself mean you are signed in.

SI Desk removes `CURSOR_API_KEY`, `CURSOR_AUTH_TOKEN`, and `CURSOR_API_ENDPOINT` from the connector process. Putting a Cursor key in the environment does not sign this app in.

The setup check treats you as signed in when your Windows profile's `.cursor` folder contains `cli-config.json` or `auth.json` with a saved Cursor credential. The check never shows that value. Chat still needs the next step before it can send.

#### 3. Connect / refresh models

1. Click **Connection**.
2. Click **Connect / refresh models**.
3. Choose a model in the model dropdown. Names from Cursor are prefixed with **Cursor ·** so you can tell them apart from the other providers.

With no folder open, a good connect says **Cursor chat connected. Open Folder to work on files.** That chat cannot use tools or edit files. SI Desk gives that session an empty workspace and tells the connector to refuse shell, file, and web tools.

If connect fails, the status line names the next step. The exact sentences are in [Troubleshooting](#troubleshooting).

### Grok Build

Grok Build is xAI's `grok` command-line program. SI Desk does not download it.

#### 1. Install grok.exe

xAI's Windows install is one PowerShell command. It is written up in xAI's getting started guide: the [copy in the Grok Build repository](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-pager/docs/user-guide/01-getting-started.md), and the online docs at [docs.x.ai/build/overview](https://docs.x.ai/build/overview). The same command is on [x.ai/cli](https://x.ai/cli). The script that command runs is [x.ai/cli/install.ps1](https://x.ai/cli/install.ps1) ([repository copy](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-pager/scripts/install.ps1)).

1. Open PowerShell.
2. Run:

```powershell
irm https://x.ai/cli/install.ps1 | iex
```

3. Open a new PowerShell window and run `grok --version`. A version line means the program is installed.

That installer writes `grok.exe` to `%USERPROFILE%\.grok\bin\grok.exe` and adds `%USERPROFILE%\.grok\bin` to your user PATH. xAI's getting started guide says the same thing about the PATH.

SI Desk uses the first `grok.exe` it can find:

1. The file named by the `XAI_DESK_GROK` environment variable, if that file exists.
2. `%USERPROFILE%\.grok\bin\grok.exe`
3. `grok.exe` on PATH

Leave `XAI_DESK_GROK` unset. The default install is the second file in that list. The next **Check** or **Connect** reads that file from disk. If the installer was given another folder (`GROK_BIN_DIR`), it adds that folder to your user PATH, which is the third place above. A copy of SI Desk that is already open keeps the PATH it started with. Quit SI Desk and start it again when you need that new PATH, or set `XAI_DESK_GROK` to the `grok.exe` file.

xAI also publishes two other Windows installs:

- Git Bash or MSYS2 can run `curl -fsSL https://x.ai/cli/install.sh | bash`. On Windows that script writes `grok.exe` to `$HOME/.grok/bin`, then tells you to add `%USERPROFILE%\.grok\bin` to PATH for `cmd.exe` and PowerShell. When those two folders are the same, SI Desk finds the file at the second path above.
- npm: `npm i -g @xai-official/grok`, from the [package notes](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-pager/npm/grok/README.md). Unless `GROK_HOME` is set, that package's installer copies `grok.exe` into `%USERPROFILE%\.grok\bin`.

If you already installed with WinGet, the getting started guide says to update that copy with `winget upgrade --id xAI.GrokBuild -e`. It also says `grok update` leaves a WinGet install unchanged. The guide does not say which folder WinGet uses. If SI Desk cannot find `grok.exe`, set `XAI_DESK_GROK` to that file, or check xAI's site.

A WSL install is the Linux program. SI Desk on Windows looks for `grok.exe`, so use the PowerShell installer above.

#### 2. Sign in

Sign-in and the account check read `%USERPROFILE%\.grok\auth.json`. The check can show the account email and a plan word (`free`, `paid`, or `unknown`). It does not show the saved secret. What that sign-in is allowed to be is in [Plans](#plans).

1. Open **Settings**, **Connections**, **Grok Build**.
2. Click **Sign in**. SI Desk runs `grok login` in a visible window. Finish that login, then the app runs a check.
3. On the chat screen, open **Connection**, choose **Grok Build**, and click **Connect / refresh models**.

**Sign in with Grok** on the Connection panel does the same `grok login`, then asks you to click **Connect**. **Sign out** in Settings runs `grok logout`. **Check** runs the account check without logging in again.

A good connect says **Grok Build connected. Open Folder to work on files.** The model list keeps Grok and xAI models. Other vendors are dropped. Labels are prefixed with **Grok Build ·**.

### xAI API

This sends chat to `https://api.x.ai/v1/`. With a folder open, the status line still ends with **This provider is chat only.**

1. Open **Settings**, **Connections**, **xAI API**.
2. Paste a key from the xAI console into **API key** and click **Save**. The box clears. The status says **Key saved**. The key is stored in Windows Credential Manager under the name `SIDesk.XaiApi` (account `default`). If that name is empty, SI Desk also reads the older name `xAIDesk.XaiApi`. The key is not written into the chat transcript.
3. On the chat screen, open **Connection**, choose **xAI API**, and click **Connect / refresh models**.

The sign-in button is disabled for this provider. A good connect says **xAI API connected. Choose a Grok model.** Only model ids that contain "grok" are listed. Labels are prefixed with **xAI API ·**.

How this key is billed is in [Plans](#plans).

## Plans

SI Desk does not sell a plan. Each connection uses an account you already have.

### Grok Build sign-in

**Sign in** runs `grok login`. xAI's authentication guide says that command opens a browser and signs in with a grok.com account, through OAuth at `auth.x.ai`. The saved session is `%USERPROFILE%\.grok\auth.json`. The guide is [Authentication](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-pager/docs/user-guide/02-authentication.md).

That guide does not tell you to sign in at the xAI console for `grok login`. The console is where an API key comes from. The guide also does not name SuperGrok, X Premium, or X Premium+ as the plan `grok login` requires.

xAI's own CLI has a separate message for when free Grok Build usage runs out. It says you have reached the free Grok Build usage limit, and it tells you to get SuperGrok, at [grok.com/supergrok](https://grok.com/supergrok). A rate limit on that grok.com sign-in says to upgrade your account. Those sentences are the CLI's user-facing text in [error.rs](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-shell/src/sampling/error.rs). They do not say whether X Premium, or any other named plan, is enough. Check xAI's site.

### xAI API key

The xAI API connection uses a key from the xAI console, [console.x.ai](https://console.x.ai). `grok login` stays the Grok Build sign-in. xAI's authentication guide says that key is a different credential from the grok.com session. The `grok` program uses the key only when no `grok login` session is stored.

xAI's CLI says a personal grok.com upgrade does not raise API team limits. Higher API limits come from credits a team admin purchases, or from a higher spend-based tier. The CLI points at [docs.x.ai/developers/rate-limits](https://docs.x.ai/developers/rate-limits#rate-limit-tiers). The same file says those limits are not a personal SuperGrok plan. Whether a new console key needs a payment method before the first call: check xAI's site.

## Open a project folder

File tools, subagents, and swarm mode need a folder, and they need **Cursor** or **Grok Build**. The xAI API provider stays chat only. With a folder open, its status line ends with **This provider is chat only.**

1. Open the folder with **File**, **Open Folder…**, or with **Connection**, then **Open Folder…**.
2. The status line says **Project folder selected. This conversation is kept; reconnect to continue in the selected folder.**
3. Click **Connect / refresh models** again.

**Close Folder** drops back to chat only and keeps the messages. Changing the folder resets the permission mode to **Always ask**.

After a folder is connected, the status line starts with **Agent connected** and names the permission mode and the folder. Cursor adds **Uses your Cursor account. Tools, subagents, and file edits stay in this folder.** Grok Build adds **Grok Build can edit files in this folder.**

The permission dropdown sits beside the **+** attach button. It appears only after a folder is open on Cursor or Grok Build.

| Mode | What the app does |
| --- | --- |
| **Always ask** | Each tool request opens a **Permission** dialog. **Allow once** or **Deny** applies to that request only. A phone, if you turned phone access on, can answer the same request. |
| **Always allow** | Cursor is started with its sandbox and its session-only approve switch. SI Desk approves a request only when Cursor offers an allow-once choice. It does not write a permanent allow list. Grok Build is started with `--always-approve`. |
| **Plan** | The agent may inspect and propose a plan. It does not implement the plan. To implement, switch to **Always ask** or **Always allow** and send a new message. |

**New conversation** starts another chat with the same provider and the same open folder.

## Chat basics

Type in **Message your agent…** and press **Enter** to send. **Shift+Enter** adds a line. The **+** button can attach files or paste a screenshot.

**Classic chat view** is on by default (rounded bubbles). Turn it off under **Settings**, **Data**, if you want the terminal transcript. In that view, **Esc** stops the reply and **Up** recalls the last message when the box is empty.

The model dropdown is empty until connect succeeds. Pick a model before you rely on a send. The send button stays off until a live model list has arrived. A saved Cursor list from an earlier session does not turn send on. After you open or close a folder, send can reconnect on the next message if a model was already chosen. The status line still asks you to reconnect.

While a reply is running, **Stop** stops the main agent and its helpers. If you type during a Cursor or Grok Build reply, the button becomes a follow-up and sends that text into the running turn.

**Changes** on the chat header opens a side panel. It lists file edits the agent reported, with the before and after text it recorded. **Subagents** on that same header opens helper cards. The **Swarm** tab on the side panel appears for a swarm run.

**Settings**, **Data**, **Line review for plans and diffs** shows a Review control on a plan or a finished edit. Saved reviews stay with the conversation if you turn the option off.

## Subagents

**Allow subagents** is on the Connection panel. It is hidden until a folder is open and the provider is **Cursor** or **Grok Build**. Turning it on reconnects the chat.

Helpers inherit this chat's mode. Plan stays read-only. Always ask still asks you. Always allow follows this chat's allow setting. **Stop** stops the main agent and the helpers. The app asks for at most three helpers at once, and at most sixteen helper identities in one session.

**Open subagents as chat panels** also opens each helper in a chat row under the transcript. The side list remains either way. **Hide** on a card hides that card. It does not stop the helper.

The header button reads **Subagents ·** plus a count. Open it to see name, model, status, and elapsed time.

## Swarm mode

**Plan big goals as a swarm (uses subagents)** is on the Connection panel, under **Allow subagents**. It shows up only when a folder is open and the provider is **Cursor** or **Grok Build**. It stays hidden on the xAI API provider, and it stays hidden before you open a folder.

The box is greyed out while **Allow subagents** is off. Checking it turns **Allow subagents** on. Each goal gets its own run board on the **Swarm** side tab.

**Swarm mode** (the dropdown next to that checkbox) is shown only for **Cursor**. It is enabled only when both **Allow subagents** and the swarm checkbox are on.

| Swarm mode | What gets hired |
| --- | --- |
| **Single model** | Every role uses the model selected in this chat, including the verifier. If that model is missing from Cursor's list, SI Desk reports an error and does not switch to another provider. |
| **Mixed models** | Roles are picked from the rankings in the profile file `swarm-role-models.json`. The verifier is chosen as a different model from the engineer when Cursor advertised one. A per-task override can force a model. |

Grok Build can run a swarm, but it does not show this dropdown.

The manager splits a large goal into tasks (engineer, investigator, researcher, scout, verifier). The scout is a second, independent lookup that cross-checks the researcher. Grok Bot is never a swarm worker; it is the grandparent above the manager, relaying status updates and handoffs to you mid-run and reviewing handoffs when that option is on. A task is not done until the verifier records a pass with a reason. **Respawn** on a swarm task stops that helper and restarts its task once, with a narrower scope. Using it again fails that task. Other workers keep going.

## Grok Bot

The left nav has **Grok Bot** (the page heading is **Grok Bot**). The tab is on unless you turn **Grokbot tab** off in Settings. The page is a mailbox: you send a message, and Grok Bot picks it up through the connector. The link line says **Off**, **Waiting**, or **Connected**.

- **Ask Grokbot to act** is the switch beside the message box. On means that message may ask Grok Bot to act inside SI Desk.
- **Stop Grokbot** pauses pickup. The button becomes **Resume Grokbot**.
- **Image** attaches a picture. You can also paste or drop one.
- **Grokbot audit** lists recent tool calls.
- **Copy** and **Like** sit on a reply.

The connector itself is off until you turn it on. In **Settings**, **Grok Bot connector**:

1. Click **Regenerate** and copy the token when it is shown. A saved token is not shown again.
2. Turn the connector **On**.
3. **This PC only** listens on `http://127.0.0.1:8790/mcp`. **Tailscale only** runs `tailscale ip -4` and binds that address on port **8790**. It does not listen on a public address.
4. **Allow Grokbot control** lets Grok Bot act when the latest message did not use **Ask Grokbot to act**. **Screenshots** is a separate switch.
5. **Test connection** and **Test inbox** check the listener and the mailbox.
6. **Wake webhook URL** and **Webhook sender key** are optional. **Save wake** stores them. The key is not shown again; the box shows dots when one is saved.

Version 0.3.66 has no Connection-panel option named **Grokbot reviews handoffs**. The swarm row in this version is the **Swarm mode** dropdown only. After a run, the manager may append a handoff block in its reply (`finding` for an observation, `verified` only when a verifier passed). That block is text in the reply, not a checkbox.

## Settings

Open **Settings** from the left nav, or **Help**, **Settings / Support**.

| Section | What you can do |
| --- | --- |
| **Connections** | Cursor **Sign in** and **Check**. Grok Build **Sign in**, **Sign out**, and **Check**. xAI API key **Save**. **Check setup**. |
| **Data** | Classic chat view, line review, the profile path, **Copy path**, **Open app data folder**, **Export support report**. |
| **Grok Bot connector** | Listener, token, Grok Bot tab, control, screenshots, avatar, wake webhook. |
| **Phone access** | Off until you turn it on. **This PC only** is `127.0.0.1:8789`. **Tailscale only** uses that PC's Tailscale address with HTTPS. |
| **Updates** | In this version the section says **Updates are off.** |
| **About** | The app name and the version string to copy into a bug report. |

**Export support report** writes a redacted zip in the profile folder. By default it leaves out source documents, chats, databases, credentials, and env files. **Include private draft content** adds drafts if you check it. Remove anything private before you send the zip to anyone.

## Where your profile and data live

The default profile is `%LOCALAPPDATA%\SIDesk`. Settings shows the real path for this Windows account. Chats, the setup-check flag, the last Cursor model list (`cursor-subscription-models.json`), and swarm role rankings live there. Grok Bot mail lives in the `grokbot` folder under that same default directory. `crash.log` in that folder is the app's crash log.

On startup, if `%LOCALAPPDATA%\SIDesk` does not exist yet and `%LOCALAPPDATA%\xAIDesk` does, SI Desk copies the old profile and leaves the old folder in place. Credential Manager reads the older names when the new names are empty.

The Cursor connector install is not inside the profile. It is under `%LOCALAPPDATA%\SovereignAI.Tools\CursorAgent\`, unless an older copy was already in the SI Desk or xAI Desk `Tools` folder.

Unzipping a new build, or replacing `SIDesk.exe`, does not delete this profile.

## Updating

In 0.3.66, **Settings** says **Updates are off.** **Check for updates** does not download a new build. `UpdaterEnabled` in the app is false.

To move to a newer release, download that release's zip from GitHub, quit SI Desk, and replace the program files. Leave `%LOCALAPPDATA%\SIDesk` where it is.

## Troubleshooting

Read the status line under the chat title, or the Cursor / Grok Build / xAI API line in **Settings**. These are the sentences the app uses. They do not include your key or a folder path.

### Models are not showing

Click **Connect / refresh models** and wait until the status line says connected. The dropdown stays empty until that succeeds.

If Cursor had a saved list from an earlier connect, a failed refresh still shows those names and adds **Showing the last saved model list. Connect again before sending.** Send stays off until a live list succeeds.

| Status line | What to do |
| --- | --- |
| The Cursor connector is not installed. Click Install Cursor, sign in with Cursor, then click Connect / refresh models. | Open **Connection** and click **Install Cursor**, or run `setup\Install-CursorConnection.ps1` as in [Install the Cursor connector](#1-install-the-cursor-connector). Then sign in. |
| Cursor is not signed in. Click Sign in with Cursor, finish in the browser, then click Connect / refresh models. | Sign in again and finish the browser step within 3 minutes. |
| Cursor auth failed. Click Sign in with Cursor, finish in the browser, then click Connect / refresh models. | The login command exited with an error. Sign in again. |
| Cursor could not be reached (network). Check this PC's connection, then click Connect / refresh models. | Check the network, then connect again. |
| Could not refresh Cursor models. Install the Cursor connector, sign in with Cursor, check the network, then click Connect / refresh models. | Do all three, then connect again. |
| Model connect timed out. Check the network, the xAI API key, Cursor sign-in, and that the Grok CLI is on PATH, then click Connect / refresh models. | The attempt was canceled or ran past the time limit. |
| No xAI API key is saved. Open Settings, paste a key from the xAI console, save it, then click Connect / refresh models. | Save the key in Settings first. |
| The xAI API rejected the saved key (auth failed). Open Settings, replace the key, save it, then click Connect / refresh models. | Replace the key. |
| SI Desk could not reach the xAI API (network). Check this PC's connection, then click Connect / refresh models. | Check the network. |
| xAI API did not advertise a Grok model. Check the account, then click Connect / refresh models. | The key worked, and the account listed no Grok model. |
| Grok CLI / Grok Build was not found on PATH. Install Grok Build, or set XAI_DESK_GROK to the grok executable, then click Connect / refresh models. | Install `grok.exe` as in [Install grok.exe](#1-install-grokexe), or set `XAI_DESK_GROK` to that file. |
| Grok Build is not signed in. Open Settings, click Sign in next to Grok Build, then click Connect / refresh models. | Sign in from Settings. |
| Grok Build auth failed. Open Settings, sign in again next to Grok Build, then click Connect / refresh models. | Sign in again. |
| Grok Build could not reach xAI (network). Check this PC's connection, then click Connect / refresh models. | Check the network. |
| Grok Build response is incomplete (silence). No answer arrived after a few seconds. … | The CLI was found but no answer arrived. Confirm install, sign-in, and network, then connect and send again. |

Choose a model advertised by that provider. SI Desk does not silently swap in a different provider's model.

### The Cursor install script failed

| What it prints | Meaning |
| --- | --- |
| Cursor runtime already exists at … It was not overwritten. | That version is already on this Windows account. Do not expect the script to replace it. Sign in and connect. If Settings still says **Connector not installed**, that folder is missing `node.exe` or `index.js`. |
| Official Cursor package does not match the pinned download. | The download was not the pinned zip. Run the script again on a stable connection. Do not point the script at a different zip. |
| Cursor package is incomplete. | The zip did not contain `dist-package\node.exe` and `dist-package\index.js`. |
| Connector missing. Install it or locate its executable, then check again. | Settings looks for `setup\Install-CursorConnection.ps1` beside `SIDesk.exe`. Keep that file next to the exe. The chat **Connection** panel runs the same script when you click **Install Cursor**. |

### Port 8789 or 8790

Nothing listens on these ports until you turn the feature on in Settings.

- **Phone access** uses port **8789**. **This PC only** is `127.0.0.1:8789`. If it cannot start, the status says **Phone access could not start. The address, port or certificate may be unavailable.** or **Phone access could not bind.** The switch turns itself off. Another program is already bound to 8789, or the Tailscale address or certificate was not available. Turn phone access off, or free that port, then try again.
- **Grok Bot connector** uses port **8790**. If it cannot start, the status says **Grok Bot connector did not bind. Nothing is listening.** or **Grok Bot connector could not start.** Turn the connector off, or free 8790, then try again.

### Other common lines

- **Canceled or timed out. Saved credentials were kept.** Sign-in or a connections action hit the 3-minute limit. Your saved key was not deleted.
- **Could not open project: …** The folder picker failed. Pick the folder again.
- **The selected project folder no longer exists.** The folder was removed or renamed. Open it again.
- **Cursor response is incomplete** or **Cursor disconnected. Reconnect before sending again.** Click **Connect / refresh models**, then send again.
- **Switch to Cursor Grok 4.7** appears when Grok Build hits a usage or rate limit. That button changes this chat to the Cursor provider.

## Report a problem

Use GitHub issues: [github.com/mikehal123/si-desk/issues/new/choose](https://github.com/mikehal123/si-desk/issues/new/choose). Pick **Bug report**.

Include:

- the version from **Settings**, **About**
- your Windows version
- whether you ran `SIDesk.exe` from the zip
- which connection you used (Cursor, Grok Build, or xAI API)
- the steps, and the status line text

Remove API keys, tokens, emails, and personal folder paths before you paste logs or screenshots. `crash.log` in the profile folder can help. The support zip from **Export support report** is safer than emailing the whole profile.

Questions can also go to the author on X: [@grokbuildAI](https://x.com/grokbuildAI).
