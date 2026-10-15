# How to use SI Desk

This guide is for someone opening SI Desk for the first time. It follows the buttons and sentences in version **0.3.67**.

The sidebar header, next to the SI logo, reads **Super Intelligence Desk**. The window title stays **SI Desk**, and the program you start is still `SIDesk.exe`. Settings, About, shows the same **Super Intelligence Desk** name and a version line. Copy that version line into a bug report.

SI Desk is an unofficial Windows desktop app in public beta. It is an independent project. It is not affiliated with or endorsed by SpaceXSI. Grok is their trademark. It is not affiliated with, endorsed by, or supported by xAI. xAI is a trademark of its owner.

You need **Windows 10 or Windows 11, 64-bit**. The release zip already contains the app runtime, so you do not install .NET yourself.

You need one way to talk to a model:

- a **Cursor** subscription on this PC, or
- the **Grok Build** command-line program (`grok.exe`) on this PC, or
- an **xAI API** key

Cursor and Grok Build can edit a project folder, run subagents, and use swarm mode. The xAI API connection is chat only.

Longer notes on plans, the Cursor installer hash, and every connect status sentence are in [Getting started](GETTING-STARTED.md). This page is the walkthrough of the screens.

## Which connection should I pick?

| Connection | What it needs | What it can do |
| --- | --- | --- |
| Cursor | A Cursor subscription on this PC, the connector install below, and Cursor sign-in | Chat. With a folder open: file edits, subagents, and swarm mode |
| Grok Build | `grok.exe` from the Grok Build Windows install, then sign-in | Chat. With a folder open: file edits, subagents, and swarm mode |
| xAI API | A key from [console.x.ai](https://console.x.ai) | Chat only. In classic chat view, the line under the title ends with **This provider is chat only.** |

## Install and first launch

1. Open the releases page: [github.com/mikehal123/si-desk/releases](https://github.com/mikehal123/si-desk/releases).
2. Download **`SIDesk-0.3.67-win-x64.zip`**. That tag can be marked as a pre-release. The zip marked Latest can be an older build. Use the 0.3.67 zip for the screens in this guide. A newer zip, if one is published later, is fine: check **Settings**, **About**, for the version.
3. Unzip the whole zip into a folder you can keep, such as a folder on your Desktop. Leave the files together. `setup\Install-CursorConnection.ps1` has to stay next to `SIDesk.exe`.
4. Double-click **`SIDesk.exe`**.

That zip is the app. You can run it from the unzipped folder.

The zip also contains `SIDesk.Setup.exe`. That program installs a copy under `%LOCALAPPDATA%\Programs\SI Desk` only when it is given a payload folder (`--payload`). If setup prints **Payload directory was not found**, close it and start `SIDesk.exe` from the unzipped folder instead.

### Windows SmartScreen

The build is unsigned. Windows SmartScreen often says it protected your PC, or that the publisher is unknown.

1. Click **More info**.
2. Click **Run anyway**.

You may see that warning the first time you open `SIDesk.exe`. It is the same warning for an unsigned app.

### The first window

The left sidebar shows **Super Intelligence Desk**, then **Chat**, **Shared room**, **Grok Bot**, and **Settings**. This build has no account row at the bottom of the sidebar. Cursor, Grok Build, and the xAI API key are separate.

The menu bar has **File**, **View**, and **Help**.

- **File** has **Open Folder…**, **Close Folder**, and **Close window**.
- **View** has **Chat**, **Shared room**, **Grok Bot**, and **Settings**.
- **Help** has **Settings / Support**.

Chat opens first. The provider starts on **Cursor**. The status line says to connect, and to open a folder when you want tools and file edits.

The first time the profile folder is new, SI Desk opens a **Setup check** window. Each line is **OK** or **Missing**. The window does not show keys or tokens.

1. Read the three lines. You only need one of them to be **OK**.
2. Click **Close** to dismiss the window, or **Check again** to run the same check.

| Line | OK means | Missing means |
| --- | --- | --- |
| xAI API key | A saved key was accepted and the API listed a Grok model | No key, the key was rejected, the network blocked the check, or no Grok model was listed |
| Cursor login | The Cursor connector files are present and a Cursor CLI sign-in file is present | The connector is not installed, or it is installed and you still need to sign in |
| Grok Build | `grok.exe` was found | The Grok CLI was not found |

The window appears once. After that, open **Settings** and click **Check setup** to see it again.

Your profile lives in `%LOCALAPPDATA%\SIDesk`. Unzipping a new build does not delete it. If that folder is missing and an older `%LOCALAPPDATA%\xAIDesk` folder exists, SI Desk copies the old profile and leaves the old folder in place.

## Connect a provider

On the chat screen, click **Connection** at the top right. The panel title is **Connection**. The dropdown is the provider. The same panel has **Connect / refresh models**, a sign-in button, **Open Folder…**, **Close Folder**, and **New conversation**.

**Connect / refresh models** asks the provider for its model list and fills the model dropdown on the chat. It gives up after about 50 seconds. A successful connect closes the panel. Sending a message stays off until a live model list comes back.

### Cursor sign-in

SI Desk does not talk to Cursor through an API key, and it does not look up `cursor` or `cursor-agent` on PATH. It starts a pinned copy of Cursor's agent package. Your Cursor account stays with Cursor. Usage follows that account.

Do these steps in order.

#### 1. Install the Cursor connector

1. Open **Connection**.
2. Leave the provider on **Cursor**.
3. If the pinned connector is missing, the panel shows **Install Cursor**. Click it.

That runs `setup\Install-CursorConnection.ps1` beside `SIDesk.exe`. It does not read account keys. When the status line says the connector is installed, go on to sign-in.

The status lines you can see:

| Status line | What to do |
| --- | --- |
| Cursor connector installed. Sign in with Cursor, then click Connect / refresh models. | Continue with sign-in below. |
| Cursor connector is already installed. Click Connect / refresh models. | The files are already there. Sign in if you have not, then connect. |
| Cursor install did not finish. Click Install Cursor to try again, then Connect / refresh models. | Click **Install Cursor** again. |
| Cursor install was canceled or timed out. Click Install Cursor to try again, then Connect / refresh models. | Click **Install Cursor** again. |
| The documented Cursor installer was not found beside the app, so the official install page was opened. | Keep `setup\Install-CursorConnection.ps1` next to `SIDesk.exe`, or install from the page that opened, then connect. |

If that script is not beside the app, **Install Cursor** opens the Cursor CLI installation page, <https://cursor.com/docs/cli/installation>, instead. A Cursor IDE install, or a `cursor-agent` command on PATH, is still not this connector.

You can also install from the unzipped folder without the button.

1. Open the unzipped folder (the one that contains `SIDesk.exe`).
2. In that folder, open PowerShell and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup\Install-CursorConnection.ps1
```

If you are running from a source checkout instead of the zip, the same script is `scripts\Install-CursorConnection.ps1`. What the script downloads, the pinned hash, and the three folders it accepts are in [Getting started](GETTING-STARTED.md#1-install-the-cursor-connector).

When the script finishes, it prints **Cursor connection installed. Open SI Desk, New chat, Cursor, then Sign in with Cursor. Uses your own Cursor account and limits. No PATH changes.**

#### 2. Sign in with Cursor

1. Start `SIDesk.exe`.
2. Click **Connection**.
3. Leave the provider on **Cursor**.
4. Click **Sign in with Cursor**.

The status line says **Complete Cursor sign-in in your browser. Waiting for Cursor…** Finish the Cursor login in the browser. SI Desk waits up to 3 minutes. When the command exits successfully, the status line says **Cursor sign-in finished. Click Connect / refresh models.**

You can do the same sign-in from **Settings**, under **Connections**, on the **Cursor** row, with **Sign in**. That row's status is only **Connector installed** or **Connector not installed**. It does not by itself mean you are signed in. After a successful Settings sign-in, the row says **Sign-in finished. Connect in Chat to refresh models.** While it waits, the row says **Waiting for Cursor sign-in…**

Putting a Cursor key in the environment does not sign this app in. SI Desk removes `CURSOR_API_KEY`, `CURSOR_AUTH_TOKEN`, and `CURSOR_API_ENDPOINT` from the connector process.

#### 3. Connect / refresh models

1. Click **Connection**.
2. Click **Connect / refresh models**.
3. Choose a model in the model dropdown on the composer. Names from Cursor are prefixed with **Cursor ·**.

With no folder open, a good connect says **Cursor chat connected. Open Folder to work on files.** That chat cannot use tools or edit files.

### Grok Build (the grok CLI)

Grok Build is the `grok` command-line program. SI Desk does not download it.

#### 1. Install grok.exe

1. Open PowerShell.
2. Run the installer published by xAI:

```powershell
irm https://x.ai/cli/install.ps1 | iex
```

3. Open a new PowerShell window and run `grok --version`. A version line means the program is installed.

That installer writes `grok.exe` to `%USERPROFILE%\.grok\bin\grok.exe`. The same command is described at [x.ai/cli](https://x.ai/cli) and in the [Grok Build getting started page](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-pager/docs/user-guide/01-getting-started.md).

SI Desk uses the first `grok.exe` it can find:

1. The file named by the `XAI_DESK_GROK` environment variable, if that file exists.
2. `%USERPROFILE%\.grok\bin\grok.exe`
3. `grok.exe` on PATH

Leave `XAI_DESK_GROK` unset unless the installer put `grok.exe` somewhere else. A copy of SI Desk that is already open keeps the PATH it started with. Quit SI Desk and start it again when you need a new PATH.

Other installers (Git Bash, npm, WinGet) are listed in [Getting started](GETTING-STARTED.md#1-install-grokexe). A WSL install is the Linux program. SI Desk on Windows looks for `grok.exe`.

#### 2. Sign in

1. Open **Settings**, **Connections**, **Grok Build**.
2. Click **Sign in**. SI Desk runs `grok login` in a visible window. Finish that login. The row says **Opening Grok sign-in…**, then **Checking…**, then an account line. The check can show an email and a plan word (`free`, `paid`, or `unknown`). It does not show the saved secret.
3. On the chat screen, open **Connection**, choose **Grok Build**, and click **Connect / refresh models**.

**Sign in with Grok** on the Connection panel does the same `grok login`. The status line says **Complete Grok sign-in, then click Connect.** After the login command finishes, it says **Grok sign-in finished. Click Connect.**

**Sign out** in Settings runs `grok logout`. **Check** runs the account check without logging in again.

A good connect says **Grok Build connected. Open Folder to work on files.** Labels in the model dropdown are prefixed with **Grok Build ·**. The list keeps Grok and xAI models.

### xAI API key

This sends chat to `https://api.x.ai/v1/`. It does not edit files.

1. Open **Settings**, **Connections**, **xAI API**.
2. Paste a key from the xAI console into **API key**. The box placeholder says **xAI API key**.
3. Click **Save**. The box clears. The status says **Key saved**. Before a key is saved, the status says **Not set**.
4. On the chat screen, open **Connection**, choose **xAI API**, and click **Connect / refresh models**.

The sign-in button is disabled for this provider. If you click through to sign-in anyway, the status says **Save the xAI API key in Settings, then Connect.**

The key is stored in Windows Credential Manager under the name `SIDesk.XaiApi` (account `default`). If that name is empty, SI Desk also reads the older name `xAIDesk.XaiApi`. The key is not written into the chat transcript.

A good connect says **xAI API connected. Choose a Grok model.** Only model ids that contain "grok" are listed. Labels are prefixed with **xAI API ·**.

## Open a project folder

File tools, subagents, and swarm mode need a folder, and they need **Cursor** or **Grok Build**.

1. Open the folder with **File**, **Open Folder…**, or with **Connection**, then **Open Folder…**.
2. The status line says **Project folder selected. This conversation is kept; reconnect to continue in the selected folder.**
3. Click **Connect / refresh models** again.

You can also name a project in the sidebar.

1. Under **Projects**, click **Create project**.
2. Type a name in **Project name**.
3. Click **Choose folder…** and pick the folder.
4. Click **Create**.

The dialog title is **Create project**. **Cancel** closes it without saving. If a response is still running, the status says **Finish or stop this response before attaching a project folder.**

**Close Folder** drops back to chat only and keeps the messages. Changing the folder resets the permission mode to **Always ask**.

After a folder is connected, the status line starts with **Agent connected** and names the permission mode and the folder. Cursor adds **Uses your Cursor account. Tools, subagents, and file edits stay in this folder.** Grok Build adds **Grok Build can edit files in this folder.**

If the folder was removed or renamed, the status says **The selected project folder no longer exists.** Open it again. If the folder picker fails, the status says **Could not open project:** followed by the reason.

## Chat basics

The box at the bottom says **Message your agent…** when classic chat view is on. That view is on by default.

1. Type your message.
2. Press **Enter** to send. **Shift+Enter** adds a line.
3. The hint under the box says **Enter to send · Shift+Enter for a new line**.

The send control is an arrow at the right of the composer. Its name is **Send message**. The tooltip says **Send message · Enter**. It stays off until a live model list has arrived. A saved list from an earlier session does not turn send on.

The model dropdown sits in the composer. Its name is **Chat model**. The tooltip says **Model for this conversation**. It is empty until connect succeeds. Pick a model before you rely on a send. Cursor names start with **Cursor ·**, Grok Build names with **Grok Build ·**, and xAI API names with **xAI API ·**.

The **+** button sits at the left of the composer. Its name is **Add attachments**. The tooltip says **Attach files or paste a screenshot**. The menu has **Attach files…** and **Paste screenshot or files**. You can also paste into the message box.

### Always ask, Always allow, and Plan

The permission dropdown sits beside the **+** button. Its name is **Agent mode and permissions**. It appears only after a folder is open on **Cursor** or **Grok Build**. It is hidden for the xAI API provider, and it is hidden before you open a folder.

| Mode | What the app does |
| --- | --- |
| **Always ask** | Each tool request opens a **Permission** dialog. The title starts with **Permission ·**. **Allow once** or **Deny** applies to that request only. The dialog says **Applies to this request only. A phone can answer this same request.** |
| **Always allow** | Cursor is started with its sandbox. SI Desk approves a request only when Cursor offers an allow-once choice. It does not write a permanent allow list. Grok Build is started with `--always-approve`. The status line includes **Always allow — tools within this project's permissions.** |
| **Plan** | The agent may inspect and propose a plan. It does not implement the plan. The status line includes **Plan · inspect this project without implementing.** To implement, switch to **Always ask** or **Always allow** and send a new message. |

Switching to **Always allow** asks you to confirm first. Always allow lets the agent run commands and edit files anywhere your Windows account can reach, not just this project folder, without asking. Only use it on projects you trust. **Turn on** applies the mode, **Cancel** keeps the previous mode, and **Don't show again** is saved for this user.

Changing this dropdown reconnects the chat.

While a reply is running, a stop icon appears. Its name is **Stop response**. The tooltip says **Stop the main agent and its helpers**. If you type during a Cursor or Grok Build reply, the arrow becomes a follow-up. Its name is **Send follow-up to the main agent**, and the tooltip says **Send follow-up · Enter**. That sends the text into the running turn.

**New conversation**, on the chat header and inside **Connection**, starts another chat with the same provider and the same open folder.

Turn **Classic chat view** off under **Settings**, **Data**, if you want the terminal transcript. In that view the hint says **Enter sends · Shift+Enter newline · Esc stops · ↑ recalls**. **Esc** stops the reply. **Up** recalls the last message when the box is empty.

When the agent has written reasoning, **Clear reasoning** can appear above the composer. It clears that response's reasoning panel. The answer and the tool activity stay.

If you scroll up, **Jump to latest ↓** appears. Click it to follow the newest text again.

**Subagents ·** and a count, and **Changes ·** and a count, sit on the chat header next to **Connection**. They open the side panel described below.

## Connection flyout: subagents, swarm, and review

These controls are inside **Connection**, under **Open Folder…**, **Close Folder**, and **New conversation**. They stay hidden until a folder is open and the provider is **Cursor** or **Grok Build**. They stay hidden on **xAI API**, and they stay hidden before you open a folder.

### Allow subagents

1. Open a folder and connect **Cursor** or **Grok Build**.
2. Open **Connection**.
3. Check **Allow subagents**.

The tooltip says: allow up to three direct helpers at once. Helpers inherit this chat's mode. Plan is read-only. Always ask requests approval. Always allow allows project tools. Stop response stops the main agent and its helpers.

Turning it on reconnects the chat. Turning it off also turns off the swarm checkbox. Up to three helpers can run at once. A further helper after sixteen identities in the session is refused.

### Swarm, Single model, and Mixed models

1. Check **Plan big goals as a swarm (uses subagents)**.
2. If **Allow subagents** was off, checking this box turns it on.
3. On **Cursor**, open the dropdown labeled **Swarm mode**.

The swarm checkbox is greyed out while **Allow subagents** is off. Each goal gets its own run board on the **Swarm** side tab.

| Swarm mode | What gets hired |
| --- | --- |
| **Single model** | Every role uses the model selected in this chat, including the verifier. If that model is missing from Cursor's list, SI Desk reports an error and does not switch to another provider. |
| **Mixed models** | Roles are picked from the rankings in the profile file `swarm-role-models.json`. The verifier is chosen as a different model from the engineer when Cursor advertised one. A per-task override can force a model. |

The tooltip on the dropdown says: **Single model uses the model selected in this chat for every worker. Mixed models hires from the role rankings and allows a per-worker override.**

The **Swarm mode** label and the dropdown are shown only for **Cursor**. They are enabled only when both **Allow subagents** and the swarm checkbox are on. Grok Build can run a swarm, and it does not show this dropdown.

Changing the swarm checkbox or the Swarm mode dropdown reconnects the chat.

The manager can split a goal into tasks with roles such as engineer, investigator, researcher, and verifier, and sometimes a Grok Bot lookup (`grokbot-research` or `grokbot-ops`). A task is not done until the verifier records a pass with a reason. **Respawn** on a swarm task stops that helper and restarts its task once, with a narrower scope. Using it again fails that task. Other workers keep going.

The **Swarm** tab says **Tasks for this goal. Expand a task to read its subagent chat.** It shows the goal, a progress bar, a count such as done and total, and an **Activity** section. **Project memory** at the top of that tab says **None saved** until notes exist, or **Open a project folder to keep notes.** when no folder is open.

### Grokbot reviews handoffs

**Grokbot reviews handoffs** is a checkbox under **Swarm mode**. It is off until you check it. The tooltip says: **After a handoff, Grokbot reviews the swarm before the next main-agent instruction. Off until checked.**

It is on the same row as the swarm controls. It shows for **Cursor** and **Grok Build** after a folder is open. It stays greyed out until both **Allow subagents** and **Plan big goals as a swarm (uses subagents)** are on. Checking it saves the choice. It does not by itself reconnect the chat.

When it is on, and the reply contains a handoff block, SI Desk asks Grok Bot to review that swarm before the next main-agent instruction. The **Swarm** tab can show:

| Text you can see | Meaning |
| --- | --- |
| **Waiting for Grokbot** | The review has started. The note under it can say **Waiting for Grokbot review**, plus the goal and how many tasks are done. |
| **Reviewed (continue)** | Grok Bot said to continue. |
| **Reviewed (fix)** | Grok Bot said to fix something. |
| **Reviewed (done)** | Grok Bot said the review is done. |
| **Grokbot review timed out** | The review wait ended. A **Continue without review** button appears next to that status. |

The wake line on the Swarm tab looks like **Wake sent** plus a time and **HTTP** plus a status code. Its name is **Grokbot wake**. If the webhook answers HTTP 400 or 401, a warning appears under it: **check wake key**.

`grokbot-research` and `grokbot-ops` do not take a model id. They are wake roles.

### Review loop cap

**Review loop cap** is the number next to that label, under the checkbox. The tooltip says: **Maximum handoff reviews for one swarm run. Default 3.**

1. Turn on **Allow subagents** and the swarm checkbox so the number is enabled.
2. Set the number from **1** to **99**. The starting value is **3**.

After that many reviews for one swarm run, SI Desk does not start another handoff review. Changing the number saves it. It does not by itself reconnect the chat.

### Open subagents as chat panels

**Open subagents as chat panels** is the last checkbox in the Connection panel, under the swarm block. The tooltip says: **Also open each helper in a chat row under the transcript. The Subagents list stays. Turn off to keep only the expandable cards.**

1. Open a folder on **Cursor** or **Grok Build**.
2. Open **Connection**.
3. Check **Open subagents as chat panels**.

Each new helper can then open as its own chat row under the transcript, with a breadcrumb so you can move between them. The side list remains either way. The box is greyed out while a reply is running. Turning it off keeps the expandable cards in the Subagents panel.

A direct reply box on a helper says **Direct reply to this helper is not available.** **Hide** on a card hides that card. It does not stop the helper. On a swarm card, **Respawn** is the retry described above.

## The Subagents panel and its Old section

1. On the chat header, click **Subagents ·** and the count.
2. The side panel opens. The tabs at the top are **Changes**, **Subagents**, and, when a swarm is running, **Swarm**. **Close** hides the panel.

The Subagents tab starts with **Helpers that are not part of a swarm.** Swarm workers stay on the **Swarm** tab.

Under that line is a summary. It starts with **Active ·** and a count. It can also list **Completed**, **Interrupted**, **Failed**, **Stopped**, **Blocked**, **Stop requested**, and **Status unknown** when those counts are above zero.

When nothing is listed, the tab says **No helpers yet.**

Each card shows:

- the helper's name (or **Subagent** when no name was reported)
- a model line
- a status chip, such as **Running**, **Finished**, **Failed**, **Stopped**, **Pending**, **Queued**, **Waiting**, **Stop requested**, **Disconnected**, **Interrupted**, **Unconfirmed**, **Blocked**, or **Status unknown**
- the elapsed time, such as `12s` or `2m 3s`, or **Not recorded** when the clock was not saved

**Hide** is on the card. The tooltip says **Hide this card. Does not stop the agent.** Hidden cards leave the list. A **Closed agents ·** button appears with the count. Open it and click the helper's name to put the card back. While every card is hidden, the empty line says **Reopen a card from Closed agents.**

Expand a card to read **Task**, **Result**, **Failure** when something failed, **Log** (including **Latest tool** and **Diagnostics**), and **Thinking** when reasoning was recorded. An empty running card says **No response yet.** A finished card with no text says **No response recorded.**

### Old

Helpers that have ended, and helpers that disconnected, move into a collapsed section labeled **Old**. The header becomes **Old ·** plus the number of cards in it. The tooltip says **Ended and disconnected helpers. Their timers are stopped.** The section stays hidden until at least one of those cards is visible.

These statuses go into **Old**:

- **Finished**
- **Failed**
- **Stopped**
- **Stop requested**
- **Disconnected**
- **Interrupted**
- **Unconfirmed**

**Blocked** and **Status unknown** stay in the live list above **Old**. A running clock keeps moving only while the helper is still working. Click the **Old** header to expand it.

## The Changes tab

1. On the chat header, click **Changes ·** and the count. Or open the side panel and click the **Changes** tab.
2. The list shows file edits the agent reported for this conversation.

When nothing has been reported, the tab says **No completed file edits reported in this conversation.** Otherwise each group is labeled **Agent turn** plus the turn number and a file count. Click a file name to read the path and the text the agent recorded. The detail says **Reported edit snapshot. Later edits may have changed this file again.** or, when a patch was recorded, **Reported file change patch. Later edits may have changed this file again.** The sections are **Before** and **After**, or **Changes** for a patch. A new file's before text is **[New file]**.

If some snapshots were dropped, the tab says how many reported file details are available and that some before/after snapshots were not retained.

**Settings**, **Data**, **Line review for plans and diffs** adds a **Review** button on each file. The tooltip says **Comment on this diff line by line**. The review panel says **Comment on a line, or mark it change this or drop this. Send review asks for those lines only. Nothing here is applied or committed.** Each line has a box, **Note for this line**. The buttons are **Send review**, **Approve all**, and **Close review**. Nothing in that panel is applied or committed until you send the review as a new instruction. Saved reviews stay with the conversation if you turn the option off.

## Shared room

**Shared room** is in the left sidebar and under **View**. The page heading is **Shared room**. The opening line says **Create a room on this PC, or paste an invitation to join. Nothing is shared until you connect.**

This build has no account sign-in. Shared room uses the name you type in **Your name**. It does not use Cursor, Grok Build, or the xAI API key as your room name.

1. In **Your name**, type the name people in the room should see. It can be 1–40 characters. The placeholder is **Shown in the room**. An empty name says **Use a name of 1–40 characters.**
2. Connect a model in Chat first. The chip on the room page says **No model connected** until you do. After that it names the provider and the model, such as **Cursor ·** plus the model id.
3. Click **Create online room** to host, or paste an invitation and click **Join room**. A successful join says **Joined.**
4. The invitation field is labeled **Invitation · about 20 minutes · one use**. The placeholder is `https://…/#…`.
5. Click **Copy invitation** and send that link to the other person. The field clears after a successful copy. The status says **Invitation copied. It expires in about 20 minutes. The field was cleared.**
6. Keep SI Desk open while you host. The status says **Hosting on this PC. Copy the invitation for the person you're inviting. It expires in about 20 minutes. Keep this app open.**

Other buttons on that page:

| Button | What it does |
| --- | --- |
| **New invitation** | Asks **Replace invitation?** **Replace and copy** makes a new one-use link and retires the old one. **Keep current** leaves the old one. |
| **Leave / stop room** | Stops or leaves. The status says **Room stopped / left. Host transcript remains in the profile; invitations and sessions are no longer usable after host shutdown.** |
| **Send to room** | Sends the text in **Message everyone in this room**. **Enter** sends. **Shift+Enter** adds a line. |
| **Ask my connected AI** | Drafts a reply from the connected model. The draft stays on your PC until you share it. |
| **Stop AI** | Cancels that draft. |
| **Share** | Sends the draft to the room. It stays disabled until a draft is ready. The preview says **Preview · only you can see this until you share**. |
| **Discard** | Drops the draft. The status says **AI draft discarded.** |
| **Jump to latest** | Shows when you have scrolled up. |

**Rooms on this PC** lists saved host transcripts. Opening one does not restart the room. The status says **Opened a host transcript from this PC.**

**Connection and privacy** says your PC hosts while the app stays open, an invitation grants access to the room history, and this build sends the name you type, not an account email. Private chats are not sent. A room holds up to 8 members and 500 messages.

Create online room uses the pinned cloudflared Windows amd64 binary **2026.9.1** (SHA-256 `2837888cc0f5d58f15b6dc478376de90b4d3ba5241c7947455d1e0a0df429712`). Release builds download that file into `room-tunnel\cloudflared.exe` beside `SIDesk.exe`. The git checkout does not contain the exe. If this install is missing it, the status says **Online rooms need the room tunnel (cloudflared.exe), which is missing from this install. Reinstall or update SI Desk.** If the checksum does not match, the status says **Room tunnel hash mismatch. Repair the app.** The room does not start in either case. If the link drops, the status says **Room connection unavailable. Retrying; an unsent message stays in the editor. Host must remain online.**

If the name is empty, create and join say **Use a name of 1–40 characters.** The Settings page does not have an account section.

## Grok Bot

**Grok Bot** is in the left sidebar. The page heading is **Grok Bot**. The tab is on unless you turn **Grokbot tab** off in Settings. If the tab is off, choosing **Grok Bot** opens Chat instead.

The page is a mailbox. You send a message here, and Grok Bot picks it up through the connector. Empty state: **No messages yet. Send one here. Grokbot picks it up through the connector.**

The line under the heading is the link:

| Line | Meaning |
| --- | --- |
| **Off** | The connector is not listening, or Grok Bot has not polled. |
| **Waiting · last poll** plus a time | The connector is up and a poll was seen, and the link is waiting. |
| **Connected · last poll** plus a time | Grok Bot has polled the connector. |

What Grok Bot can do, once the connector is on and you allow it:

- read your chats and post into a chat
- read the Shared room and post there
- list and read subagent cards
- read a swarm board, submit a swarm result, and continue a handoff review
- pick up this mailbox and reply in it
- look at the SI Desk window, click, type, and send keys inside this app
- take a screenshot of the SI Desk window only, and only when **Screenshots** is on

If control is off, the error is **Grokbot control is off. Turn on Ask Grokbot to act for the latest message, or Allow Grokbot control. Stop Grokbot blocks every action.** If screenshots are off, the error is **Screenshots are off.** While it is acting, a banner says **Grokbot is controlling the app**.

### Use the mailbox

1. Type in **Message Grokbot**.
2. Press **Enter** to send. **Shift+Enter** adds a line.
3. Click **Send**. Its name is **Send to Grokbot**.

Your message is marked **Queued**, then **Picked up**, then **Answered**. If you turned the act switch on, the footer also says **Act on**.

The switch beside the message box is named **Ask Grokbot to act**. That is its tooltip. On means that message may ask Grok Bot to act inside SI Desk. Off means the message is for a reply, unless **Allow Grokbot control** is on in Settings.

**Image** attaches a picture. You can also paste or drop one. A bad file shows **wrong type**.

**Grokbot audit** lists recent tool calls. Empty state: **No tool calls yet.**

On a reply, **Copy** copies that message (it can briefly say **Copied**), and **Like** marks it (the name becomes **Liked**). The header menu has **Copy conversation**.

### The connector

The connector is off until you turn it on. Open **Settings**, **Grok Bot connector**. The note says it lets Grok Bot read and post in this app, and pick up the Grok Bot tab. Off until you turn it on.

The connector token and the webhook sender key are two different secrets. The token is what Grok Bot sends to SI Desk. The webhook sender key is what SI Desk sends when it tries to wake Grok Bot.

1. Click **Regenerate**. The status says **New token. Copy it now. It is not shown again.**
2. Click **Copy**. The status says **Token copied. It will not be shown again after you leave this screen.** Put that token in the Grok Bot side that calls this app. A saved token is not shown again. If you click **Copy** with nothing on screen, the status says **Generate a token to copy it. A saved token is not shown again.**
3. Turn the connector switch **On**. Its name is **Grok Bot connector**.
4. Choose a bind mode. **This PC only** listens on `http://127.0.0.1:8790/mcp`. **Tailscale only** runs `tailscale ip -4` and binds that address on port **8790**. It does not listen on a public address.
5. The address line shows the URL. A good start says **On. This PC only.** or **On. Tailscale only.** Before that, you may see **Starting…** Off says **Off. Nothing is listening.** If you turn it on with no token, the status says **Generate a token first. Nothing is listening.**

Other controls in that section:

| Control | What it does |
| --- | --- |
| **Revoke** | Deletes the token, turns the connector off, and says **Token revoked. Off. Nothing is listening.** |
| **Test connection** | Calls the listener with the saved token. Success starts with **Connected.** Failure starts with **Test failed.** If nothing is listening, it says **Off. Nothing is listening.** |
| **Test inbox** | Queues a ping in the Grok Bot tab. The status says **Queued a ping in the Grokbot tab.** |
| **Grokbot tab** | On shows **Grok Bot** in the sidebar. Off hides it. |
| **Allow Grokbot control** | Lets Grok Bot act even when the latest mailbox message did not use **Ask Grokbot to act**. |
| **Screenshots** | Allows a screenshot of the SI Desk window. |
| **Grokbot avatar** | Picks the mark used in the sidebar and on replies. |
| **Wake webhook URL** | Optional address SI Desk calls to wake Grok Bot. |
| **Webhook sender key** | Optional secret sent with that wake. |
| **Save wake** | Stores the URL and, if you typed one, the key. The status says **Wake saved**. |
| **Test wake** | Sends a test wake. |
| **Clear wake key** | Removes the saved sender key. |

The key box does not show a saved key again. When one is saved, the placeholder is a row of dots. The eye button next to the box is **Show webhook sender key** while you are typing a new key, and **Hide webhook sender key** after you reveal it. It stays disabled until you type a key. The tooltip when it is disabled says **Type a new key to show it**.

### Stop Grokbot

**Stop Grokbot** is on the Grok Bot page, under the message box. Click it to pause pickup. The button becomes **Resume Grokbot**. Click that to allow pickup again.

Stop blocks every Grok Bot action, including ones that would have been allowed by **Ask Grokbot to act** or **Allow Grokbot control**.

## Settings, About, and updates

Open **Settings** from the left sidebar, from **View**, or from **Help**, **Settings / Support**. The page subtitle says **Connections, data, and updates on this PC.**

| Section | What you can do |
| --- | --- |
| **Connections** | Cursor **Sign in** and **Check**. Grok Build **Sign in**, **Sign out**, and **Check**. xAI API **API key** and **Save**. **Check setup**. |
| **Data** | **Classic chat view**. **Line review for plans and diffs**. The profile path. **Copy path**, **Open app data folder**, **Export support report**. |
| **Subagent skill library** | A checkbox, **Subagent skill library**. Cards stay on this PC. **Export library** and **Import**. Empty state: **No skill cards yet.** |
| **Phone access** | Off until you turn it on. **This PC only** is `127.0.0.1:8789`. **Tailscale only** uses that PC's Tailscale address with HTTPS. **New pairing code** starts a pairing. **Notify this phone when a task finishes or needs approval.** |
| **Grok Bot connector** | The listener, token, tab, control, screenshots, avatar, and wake settings above. |
| **Updates** | The section says **Updates are off.** |
| **About** | **Super Intelligence Desk** and **Version** plus the version string. |

**Export support report** writes a redacted zip. The dialog has **Include private draft content**, **Export**, and **Cancel**. By default the zip leaves out source documents, chats, databases, credentials, and env files. Checking **Include private draft content** adds drafts. Remove anything private before you send the zip to anyone.

**Copy path** says **Profile path copied.**

### Updates

In this version the Updates section says **Updates are off.** There is no download button on that page. `UpdaterEnabled` in the app is false.

To move to a newer release:

1. Download that release's zip from GitHub.
2. Quit SI Desk.
3. Replace the program files.
4. Leave `%LOCALAPPDATA%\SIDesk` where it is.

Then start `SIDesk.exe` again and check **Settings**, **About**, for the new version line.

## Troubleshooting

Read the status line under the chat title, or the Cursor, Grok Build, and xAI API lines in **Settings**. These sentences do not include your key or a folder path. The full connect table is in [Getting started](GETTING-STARTED.md#troubleshooting).

### Port 8790 is busy

The Grok Bot connector listens on port **8790** only after you turn it on. **This PC only** uses `127.0.0.1`. **Tailscale only** uses the Tailscale address from `tailscale ip -4`. Nothing else in SI Desk should bind that port.

If another program already has 8790, the connector cannot start. The usual cause is another SI Desk window, or an older xAI Desk window, that still has **Grok Bot connector** turned on.

1. Quit the other desk. Close its window so it is not still running.
2. In the desk you want to keep, open **Settings**, **Grok Bot connector**.
3. Turn the connector **Off**. The status should say **Off. Nothing is listening.**
4. Turn it **On** again.

There is no button named close other desks. Quitting the other app is what frees the port.

When the bind fails, the status is one of these:

- **Grok Bot connector could not start.** followed by one line from the system (often that the address is already in use)
- **Grok Bot connector did not bind.** plus the address and port
- **Grok Bot connector did not bind. Nothing is listening.**

The on/off choice can stay saved as on even though nothing is listening. Turn it off and on again after the port is free.

**Tailscale only** has its own sentences when Tailscale is missing or `tailscale ip -4` fails, including **Tailscale is not installed. The connector stayed off.** Phone access uses port **8789**, not 8790. A phone bind failure says **Phone access could not start. The address, port or certificate may be unavailable.** or **Phone access could not bind.**

### The connector token does not match

Grok Bot must send the token from **Regenerate**. SI Desk compares it with the token in Windows Credential Manager (`SIDesk.GrokBotConnector`, account `token`). If the presented token is missing or different, the connector answers **HTTP 401** and writes **rejected** in the connector audit. The app does not show a dialog titled connector key mismatch.

1. Open **Settings**, **Grok Bot connector**.
2. Click **Test connection**. A mismatch looks like **Test failed. HTTP 401.**
3. Click **Regenerate**.
4. Click **Copy** while the new token is on screen.
5. Paste that same token into the Grok Bot side that calls SI Desk. A token you copied earlier no longer works after **Regenerate** or **Revoke**.
6. Turn the connector **On** if it is off, and test again. Success starts with **Connected.**

This token is not the webhook sender key and not the xAI API key.

### Webhook sender key is not set

**Webhook sender key is not set.** means SI Desk tried to wake Grok Bot and no sender key is saved, and you did not type one in the box.

You see that sentence when:

- you click **Test wake** with an empty key box and no saved key
- a wake from the Grok Bot tab or from a swarm handoff review runs without a key

The same words can appear as a note under the mailbox message.

1. Open **Settings**, **Grok Bot connector**.
2. Fill **Wake webhook URL** with an `http` or `https` address. A bad URL says **Wake URL must be http or https with no user info.** An empty URL says **Wake webhook URL is not set.**
3. Type the sender key into **Webhook sender key**.
4. Click **Save wake**. The status says **Wake saved**. The box clears and shows dots when a key is stored.
5. Click **Test wake**. A success says **Test sent: HTTP** plus the code, when the webhook answers and the note is **Grokbot pinged**.

**Clear wake key** removes it. The status then says **Key not set**, or **Wake key could not be cleared.** if the stored key is still there. A key that failed to save says **Wake key could not be saved.** A key SI Desk will not store says **Webhook sender key is not valid.**

If the Swarm tab says **check wake key**, the webhook answered HTTP 400 or 401. The saved sender key was sent, and the server rejected it. Paste the key the webhook expects, click **Save wake**, and try again. That warning is about the webhook sender key, not the connector token from **Regenerate**.

### Signing back in

Sign in again when a status line says you are signed out or the login failed. Saved chats stay in the profile.

**Cursor**

1. Open **Connection** and leave the provider on **Cursor**.
2. Click **Sign in with Cursor**.
3. Finish in the browser within 3 minutes.
4. Click **Connect / refresh models**.

The status line **Cursor is not signed in. Click Sign in with Cursor, finish in the browser, then click Connect / refresh models.** means the sign-in file is missing. **Cursor auth failed. Click Sign in with Cursor, finish in the browser, then click Connect / refresh models.** means the login command exited with an error. Do the same four steps.

From Settings, the Cursor row's **Sign in** button does the same browser login. Then connect in Chat. The row does not refresh models by itself.

**Grok Build**

1. Open **Settings**, **Connections**, **Grok Build**.
2. Click **Sign in**.
3. Finish `grok login`.
4. On the chat screen, choose **Grok Build** and click **Connect / refresh models**.

**Grok Build is not signed in. Open Settings, click Sign in next to Grok Build, then click Connect / refresh models.** and **Grok Build auth failed. Open Settings, sign in again next to Grok Build, then click Connect / refresh models.** both mean do those steps. **Sign in with Grok** on the Connection panel is the same login.

**Shared room**

This build has no shared-room account and no **Sign in with browser** button. Type a name on the Shared room page. That does not sign you in or out of Cursor, Grok Build, or the xAI API.

### Models are not showing

Click **Connect / refresh models** and wait until the status line says connected. The dropdown stays empty until that succeeds.

If Cursor had a saved list from an earlier connect, a failed refresh still shows those names and adds **Showing the last saved model list. Connect again before sending.** Send stays off until a live list succeeds.

| Status line | What to do |
| --- | --- |
| The Cursor connector is not installed. Click Install Cursor, sign in with Cursor, then click Connect / refresh models. | Install, then sign in. |
| No xAI API key is saved. Open Settings, paste a key from the xAI console, save it, then click Connect / refresh models. | Save the key, then connect. |
| The xAI API rejected the saved key (auth failed). Open Settings, replace the key, save it, then click Connect / refresh models. | Replace the key. |
| Grok CLI / Grok Build was not found on PATH. Install Grok Build, or set XAI_DESK_GROK to the grok executable, then click Connect / refresh models. | Install `grok.exe`, or set `XAI_DESK_GROK` to that file, then quit and start SI Desk if PATH changed. |
| Model connect timed out. Check the network, the xAI API key, Cursor sign-in, and that the Grok CLI is on PATH, then click Connect / refresh models. | Check those, then connect again. |
| Cursor response is incomplete, with the stop reason in parentheses. | Click **Connect / refresh models**, then send again. |
| Cursor disconnected. Reconnect before sending again. | Click **Connect / refresh models**, then send again. |

Choose a model advertised by that provider. SI Desk does not silently swap in a different provider's model.

**Switch to Cursor Grok 4.7** can appear when Grok Build hits a usage or rate limit. That button changes this chat to the Cursor provider.

## Report a problem

Use GitHub issues: [github.com/mikehal123/si-desk/issues/new/choose](https://github.com/mikehal123/si-desk/issues/new/choose). Pick **Bug report**.

Include:

- the version from **Settings**, **About**
- your Windows version
- whether you ran `SIDesk.exe` from the zip
- which connection you used (Cursor, Grok Build, or xAI API)
- the steps, and the status line text

Remove API keys, tokens, emails, and personal folder paths before you paste logs or screenshots. `crash.log` in the profile folder can help. The support zip from **Export support report** is safer than sending the whole profile.

Questions can also go to the author on X: [@grokbuildAI](https://x.com/grokbuildAI).
