"""Build a separate, shareable team preview with selected QA evidence, never a user profile."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--published', required=True, type=Path)
parser.add_argument('--output', required=True, type=Path)
parser.add_argument('--version', required=True)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[1]
archive = Path(str(args.output) + '.zip')
if archive.exists():
    raise SystemExit('Refusing to overwrite an existing ZIP')
subprocess.run([sys.executable, str(repo / 'scripts/package_review_candidate.py'), '--published', str(args.published),
                '--output', str(args.output), '--version', args.version], check=True)
wrapper = args.output / 'SovereignAI.ps1'
wrapper.write_text(wrapper.read_text(encoding='utf-8-sig').replace("'qa-cw-candidate'", "'qa-team-20260921'"), encoding='utf-8-sig')
shutil.copy2(repo / 'docs/desktop/TEAM-PREVIEW-SETUP.md', args.output / 'START-HERE.md')
evidence = args.output / 'Shared-QA'
evidence.mkdir()
for name in ('codex-local-20260921', 'ollama-local-20260921'):
    source = repo / 'docs/desktop/evidence' / name
    for path in source.rglob('*'):
        if path.is_file() and path.suffix.lower() in {'.md', '.json', '.csv', '.png', '.txt', '.py', '.trx'}:
            target = evidence / name / path.relative_to(source)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
tools = args.output / 'QA-Tools'
tools.mkdir()
shutil.copy2(repo / 'scripts/verify_ollama_qa.ps1', tools / 'verify_ollama_qa.ps1')
for name in ('CODEX-CANDIDATE-REVIEW.md', 'CODEX-GUEST-CANDIDATE-ACCEPTANCE.md'):
    shutil.copy2(repo / 'docs/desktop/evidence/connected-workspace-grok' / name, evidence / name)
identity_path = args.output / 'CANDIDATE.json'
identity = json.loads(identity_path.read_text())
identity.update(purpose='Team preview: local model completion fixes; see START-HERE.md for exact acceptance scope',
                instance='qa-team-20260921', source_worktree_dirty=bool(subprocess.check_output(['git','status','--porcelain'],cwd=repo).strip()),
                source_files={str(p.relative_to(repo)):hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted((repo / 'apps/desktop').rglob('*.cs')) if not {'obj','bin'} & set(p.parts)})
identity_path.write_text(json.dumps(identity, indent=2), encoding='utf-8')
manifest=[]
for path in sorted(args.output.rglob('*')):
    if not path.is_file() or path == args.output / 'files.json': continue
    if path.name.lower() in {'.env','auth.json','credentials.json','transcript.json'} or path.suffix.lower() in {'.db','.sqlite','.sqlite3'} or {'codex-home','chat-workspace','profile'} & set(path.relative_to(args.output).parts):
        raise SystemExit(f'User state is not allowed in this package: {path}')
    manifest.append(dict(path=str(path.relative_to(args.output)),bytes=path.stat().st_size,sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
(args.output / 'files.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as zip:
    for path in sorted(args.output.rglob('*')):
        if path.is_file(): zip.write(path,path.relative_to(args.output).as_posix())
with zipfile.ZipFile(archive) as zip:
    if zip.testzip() is not None: raise SystemExit('ZIP integrity check failed')
    rows=json.loads(zip.read('files.json'))
    for row in rows:
        data=zip.read(row['path'].replace('\\','/'))
        if len(data)!=row['bytes'] or hashlib.sha256(data).hexdigest()!=row['sha256']:
            raise SystemExit('Archived payload mismatch: '+row['path'])
result=dict(zip=str(archive),bytes=archive.stat().st_size,sha256=hashlib.sha256(archive.read_bytes()).hexdigest(),verified_files=len(manifest))
archive.with_suffix('.sha256.txt').write_text(result['sha256']+'  '+archive.name+'\n',encoding='utf-8')
print(json.dumps(result))
