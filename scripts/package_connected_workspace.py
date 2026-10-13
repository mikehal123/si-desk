"""Assemble a new local beta; preserve all previous release directories."""
from pathlib import Path
import hashlib
import json
import shutil

repo = Path(__file__).resolve().parents[1]
release = Path('D:/xAIDesk-Windows-0.3.4')
if release.exists():
    raise SystemExit('Release folder already exists; refusing to overwrite it.')
release.mkdir()
shutil.copytree(repo / 'artifacts/capabilities-034/app', release / 'payload')
previous = Path('D:/xAIDesk-Windows-0.3.3')
for name in ['Setup.exe', 'Install.cmd', 'Launch.cmd', 'Repair.cmd', 'Uninstall.cmd', 'CHAT-CONNECTIONS.md', 'IMIGROK-MIT-NOTICE.txt']:
    shutil.copyfile(previous / name, release / name)
installer = (previous / 'SovereignAI.ps1').read_text(encoding='utf-8-sig')
installer = installer.replace("$version='0.3.3'", "$version='0.3.4'")
installer = installer.replace('xAI Desk 0.3.3 — shared room and options workbench beta', 'xAI Desk 0.3.4 — connected workspace beta')
(release / 'SovereignAI.ps1').write_text(installer, encoding='utf-8-sig')
shutil.copyfile(repo / 'docs/desktop/CONNECTED-WORKSPACE-PROJECT.md', release / 'INTEGRATION-PROJECT.md')
(release / 'START-HERE.md').write_text('''# xAI Desk 0.3.4 — connected workspace beta

Run Install.cmd to update xAI Desk while retaining its profile. Open the usual xAI Desk shortcut.

The app opens on Capabilities. Choose Check local connections, then Load saved CN findings to inspect existing lookup inside the app. Choose Open options workbench to run bundled Python calculations, or Open lookup and Check sources · no model to inspect your universe inputs.

CN is a read-only connection to an already-running local service on port 8764. Ollama model discovery uses port 11434. Neither service nor model weights are bundled. On another PC these features require setup; failure is shown explicitly. This build does not run CN scans, change verdicts, or generate an AI answer during the connection check.

The lookup beta from 0.3.3 is included. Full model-assisted lookup needs a connected account/model. Full blind/directed scans, integrated memory, saved cross-tool cases and Lab execution remain in INTEGRATION-PROJECT.md. This is an incremental working beta, not completion of that project.

Use Repair.cmd to restore application files. Uninstall.cmd keeps profile data. Previous installers remain unchanged. Windows x64 only; broader clean-machine/Pro qualification is not claimed.
''', encoding='utf-8')
files = []
for path in sorted(release.rglob('*')):
    if path.is_file():
        if path.name.lower() in {'.env', 'auth.json', 'credentials.json', 'transcript.json'} or path.suffix.lower() in {'.db', '.sqlite'}:
            raise SystemExit('Unexpected user state in package: ' + str(path))
        with path.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        files.append(dict(path=str(path.relative_to(release)), sha256=digest, bytes=path.stat().st_size))
(release / 'files.json').write_text(json.dumps(files, indent=2), encoding='utf-8')
print(json.dumps(dict(package=str(release), files=len(files))))
