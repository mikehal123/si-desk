"""Package a published review candidate without modifying the original release."""
import argparse
import hashlib
import json
import re
from pathlib import Path
import shutil
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--published', required=True, type=Path)
parser.add_argument('--output', required=True, type=Path)
parser.add_argument('--version', required=True)
args = parser.parse_args()
if not re.fullmatch(r'[0-9A-Za-z.-]+', args.version):
    raise SystemExit('Invalid candidate version')
repo = Path(__file__).resolve().parents[1]
baseline = Path('D:/xAIDesk-Windows-0.3.4')
if args.output.exists():
    raise SystemExit('Refusing to overwrite an existing package')
if not (args.published / 'SovereignAI.DesktopPreview.exe').is_file():
    raise SystemExit('Published app is missing')
args.output.mkdir(parents=True)
shutil.copytree(args.published, args.output / 'payload')
shutil.copytree(baseline / 'payload/engine-bundle', args.output / 'payload/engine-bundle')
for name in ('Setup.exe', 'Install.cmd', 'Launch.cmd', 'Repair.cmd', 'Uninstall.cmd',
             'CHAT-CONNECTIONS.md', 'IMIGROK-MIT-NOTICE.txt'):
    shutil.copy2(baseline / name, args.output / name)
installer = (baseline / 'SovereignAI.ps1').read_text(encoding='utf-8-sig')
installer = installer.replace("$version='0.3.4'", f"$version='{args.version}'")
installer = installer.replace("[string]$Instance='main'", "[string]$Instance='qa-cw-candidate'")
installer = installer.replace("$ErrorActionPreference='Stop'", "$ErrorActionPreference='Stop'\nif ($Instance -eq 'main') { throw 'Review candidate requires a QA instance.' }")
installer = installer.replace('    Set-Content -LiteralPath $profileRecord',
    '    New-Item -ItemType Directory -Path $profile -Force | Out-Null\n    Set-Content -LiteralPath $profileRecord')
(args.output / 'SovereignAI.ps1').write_text(installer, encoding='utf-8-sig')
shutil.copy2(repo / 'docs/desktop/CONNECTED-WORKSPACE-PROJECT.md', args.output / 'INTEGRATION-PROJECT.md')
identity = {'version': args.version, 'source_commit': subprocess.check_output(
    ['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip(),
    'baseline': str(baseline), 'purpose': 'QA candidate; live UI acceptance pending',
    'installer': 'Reused baseline Setup.exe with QA-only wrapper and profile creation'}
(args.output / 'CANDIDATE.json').write_text(json.dumps(identity, indent=2), encoding='utf-8')
files = []
for path in sorted(args.output.rglob('*')):
    if not path.is_file():
        continue
    if path.name.lower() in {'.env', 'auth.json', 'credentials.json', 'transcript.json'} or path.suffix.lower() in {'.db', '.sqlite'}:
        raise SystemExit(f'Unexpected user state: {path}')
    with path.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    files.append({'path': str(path.relative_to(args.output)), 'sha256': digest, 'bytes': path.stat().st_size})
(args.output / 'files.json').write_text(json.dumps(files, indent=2), encoding='utf-8')
print(json.dumps({'package': str(args.output), 'files': len(files), **identity}))
