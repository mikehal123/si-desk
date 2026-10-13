"""Assemble the combined release from a known prior package and a fresh publish.
Signing and publication are separate review-gated steps.
"""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--previous', type=Path, required=True)
parser.add_argument('--publish', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
package = args.output / 'xAIDesk-Windows-0.3.29'
if package.exists():
    raise SystemExit('Use a fresh package output to avoid stale files.')
if not (args.publish / 'valuation-runtime-files.json').is_file():
    raise SystemExit('Stage the valuation runtime before packaging.')
shutil.copytree(args.previous, package)
shutil.copytree(args.publish, package / 'payload', dirs_exist_ok=True)
for path in package.iterdir():
    if path.is_file() and path.suffix.lower() in {'.md', '.ps1', '.cmd'} and not path.name.startswith('RELEASE-NOTES'):
        path.write_text(path.read_text(encoding='utf-8-sig').replace('0.3.28', '0.3.29'), encoding='utf-8')
notes = '''# xAI Desk 0.3.29 — chat layout and local valuation

- Show the working spinner inside the model picker, with no separate progress row.
- Align user and assistant messages with the composer, including the Changes panel layout.
- Explain image support for the selected connection; show saved-image guidance beside actual images.
- Hide empty copy actions on failed turns.
- Add Tools → Capabilities → Valuation, with bundled local Python and dependencies.

Valuation uses manual USD inputs and is explicitly unsaved. It does not register a lookup job, persist a case, verify input evidence or submit orders. GLM/Kimi image sending has not been added.

Existing chats, preferences and credentials remain outside the install payload. This release preserves the prior Connections, Changes, attachments, project permissions, Lab and Kimi Platform features.
'''
(package / 'RELEASE-NOTES-0329.md').write_text(notes, encoding='utf-8')
(args.output / 'release-notes-0329.md').write_text(notes, encoding='utf-8')
files = []
for path in sorted(package.rglob('*')):
    if not path.is_file() or path == package / 'files.json':
        continue
    if path.name.lower() in {'.env', 'auth.json', 'credentials.json', 'transcript.json'}:
        raise RuntimeError('Unexpected user state in payload: ' + str(path))
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    files.append(dict(path=str(path.relative_to(package)), sha256=digest, bytes=path.stat().st_size))
(package / 'files.json').write_text(json.dumps(files, indent=2), encoding='utf-8')
archive = args.output / (package.name + '.zip')
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for path in package.rglob('*'):
        if path.is_file():
            z.write(path, path.relative_to(args.output))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    for row in files:
        data = z.read(package.name + '/' + row['path'].replace('\\', '/'))
        assert len(data) == row['bytes'] and hashlib.sha256(data).hexdigest() == row['sha256']
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
Path(str(archive) + '.sha256').write_text(digest + '  ' + archive.name + '\n')
receipt = dict(files=len(files), bytes=archive.stat().st_size, sha256=digest, archive=str(archive))
(args.output / 'package-receipt.json').write_text(json.dumps(receipt, indent=2))
print(json.dumps(receipt))
