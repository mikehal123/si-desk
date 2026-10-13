# Isolated September 21 QA installer

This is a one-candidate, click-to-install wrapper for VM acceptance, using the existing SetupLib directly. It never opens a command shell. It embeds the pinned review ZIP and checks its SHA-256, every `files.json` record, and the installed desktop executable before launch.

The only destination is `%LOCALAPPDATA%\SovereignAI.DesktopPreview\qa-cw-guestcandidate`. An existing instance is refused. The main app and `qa-034guest` are preserved. The receipt is `candidate-install-receipt.json` in the new instance directory. No model, scan, account sign-in or service installation is performed.

Build from the repository root with the task-local SDK:

```powershell
.tools\dotnet\dotnet.exe publish apps\desktop\Sovereign.Desktop.QaInstaller -c Release -r win-x64 --self-contained true -p:CandidateZip=D:\grok-computer\transfer\sai-034-candidate-20260921.zip -o artifacts\codex-candidate-20260921\native-installer
```

Run the generated executable with `--verify-only` for archive/manifest validation without installation. Other arguments are refused. The GUI installs only after its Install QA candidate button is clicked.

Validated build distributed as `D:\grok-computer\transfer\xAI Desk-QA-Candidate.exe`: 320683426 bytes, SHA-256 `507cf77378ac235095f44b2c53207e9ddd7f16b099a85ee3869652175fe9a3ea`. The candidate payload identity is recorded in `docs/desktop/evidence/connected-workspace-grok/CODEX-CANDIDATE-REVIEW.md`; this wrapper does not change that payload.
