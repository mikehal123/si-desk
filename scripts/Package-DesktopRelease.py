"""Assemble the combined release from a known prior package and a fresh publish.
Signing and publication are separate review-gated steps.
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import zipfile
from desktop_package_layout import assemble_payload, verify_inventory
from desktop_component_inventory import create_inventory
from desktop_desk_compatibility import verify as verify_desk_compatibility

parser = argparse.ArgumentParser()
parser.add_argument('--previous', type=Path, required=True)
parser.add_argument('--publish', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--version', required=True)
parser.add_argument('--previous-version', required=True)
parser.add_argument('--notes', type=Path, required=True)
parser.add_argument('--documents', type=Path)
parser.add_argument('--setup', type=Path, help='Fresh self-contained Setup.exe built from this source pin.')
parser.add_argument('--nuget-cache', type=Path, default=Path.home() / '.nuget/packages', help='Exact restored NuGet package metadata and license files (build-time only).')
parser.add_argument('--retain-component', action='append', default=[], help='Explicit path under the prior payload to retain, verified against its files.json. Must not overlap the fresh publish.')
parser.add_argument('--finalize-staging', action='store_true', help='Rebuild manifest/archive of this existing, unpublished staging package after documentation changes.')
args = parser.parse_args()
if not all(re.fullmatch(r'\d+\.\d+\.\d+', v) for v in (args.version, args.previous_version)):
    raise SystemExit('Use numeric release versions.')
if tuple(map(int, args.version.split('.'))) >= (0, 3, 35) and not args.setup:
    raise SystemExit('A fresh installer is required; do not retain the old prerequisite/bootstrap implementation.')
if tuple(map(int, args.version.split('.'))) >= (0, 3, 35):
    verify_desk_compatibility(args.publish, args.version)
    updater = args.publish / 'updater/SovereignAI.Updater.exe'
    if not updater.is_file() or updater.is_symlink():
        raise SystemExit('A freshly published updater is required; do not retain the old update guard.')
    subprocess.run([
        'powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
        str(Path(__file__).with_name('Verify-DesktopSetupVersion.ps1')),
        '-Path', str(updater.resolve()), '-ExpectedVersion', args.version,
    ], check=True)
if args.setup:
    if not args.setup.is_file() or args.setup.is_symlink():
        raise SystemExit('Fresh installer does not exist or is a link.')
    subprocess.run([
        'powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
        str(Path(__file__).with_name('Verify-DesktopSetupVersion.ps1')),
        '-Path', str(args.setup.resolve()), '-ExpectedVersion', args.version,
    ], check=True)
subprocess.run([
    'powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
    str(Path(__file__).with_name('Verify-DesktopReleaseVersion.ps1')),
    '-AppDirectory', str(args.publish.resolve()), '-ExpectedVersion', args.version,
], check=True)
package = args.output / ('xAIDesk-Windows-' + args.version)
if package.exists() and not args.finalize_staging:
    raise SystemExit('Use a fresh package output to avoid stale files.')
if not (args.publish / 'valuation-runtime-files.json').is_file():
    raise SystemExit('Stage the valuation runtime before packaging.')
if tuple(map(int, args.version.split('.'))) >= (0, 3, 33):
    runtime = args.publish / 'desk-runtime'
    inventory_path = runtime / 'qa-files.json'
    if not inventory_path.is_file():
        raise SystemExit('Stage the reviewed-case runtime before packaging.')
    inventory_bytes = inventory_path.read_bytes()
    if inventory_bytes not in (args.publish / 'SovereignAI.DesktopPreview.dll').read_bytes():
        raise SystemExit('Reviewed-case runtime inventory is not embedded in this application build.')
    verify_inventory(runtime, json.loads(inventory_bytes.decode('utf-8-sig')), ('qa-files.json',))
    build_metadata = runtime / 'desk/runtime/build-info.json'
    if build_metadata.exists():
        build = json.loads(build_metadata.read_text(encoding='utf-8-sig'))
        if build.get('source_dirty') is not False or not re.fullmatch('[0-9a-f]{40}', build.get('source_commit', '')):
            raise SystemExit('Release runtime must be staged from committed source.')
    elif tuple(map(int, args.version.split('.'))) >= (0, 3, 34):
        raise SystemExit('Release runtime source identity is missing.')
def verify_document_helper(directory):
    component = directory / 'document-extractor'
    if tuple(map(int, args.version.split('.'))) < (0, 3, 34) and not component.exists():
        return
    manifest = directory / 'document-extraction-files.json'
    if not manifest.is_file() or not component.is_dir():
        raise SystemExit('Stage the self-contained document helper before packaging.')
    inventory_bytes = manifest.read_bytes()
    if inventory_bytes not in (directory / 'SovereignAI.DesktopPreview.dll').read_bytes():
        raise SystemExit('Document helper inventory is not embedded in the application.')
    paths = set()
    for row in json.loads(inventory_bytes.decode('utf-8-sig')):
        target = component / row['Path']
        resolved = target.resolve()
        if target.is_symlink() or not resolved.is_relative_to(component.resolve()) or not target.is_file():
            raise SystemExit('Invalid document helper inventory path.')
        key = str(resolved).casefold()
        if key in paths:
            raise SystemExit('Duplicate document helper inventory path.')
        paths.add(key)
        data = target.read_bytes()
        if len(data) != row['Bytes'] or hashlib.sha256(data).hexdigest() != row['Sha256']:
            raise SystemExit('Document helper bytes do not match the embedded inventory.')
    for path in component.rglob('*'):
        if path.is_symlink() or (path.is_file() and str(path.resolve()).casefold() not in paths):
            raise SystemExit('Unexpected file or link in document helper component.')
    for name in ('Sovereign.Desktop.DocumentExtraction.exe', 'Sovereign.Desktop.DocumentExtraction.dll',
                 'Sovereign.Desktop.DocumentExtraction.deps.json', 'Sovereign.Desktop.DocumentExtraction.runtimeconfig.json',
                 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll'):
        if str((component / name).resolve()).casefold() not in paths:
            raise SystemExit('Self-contained document helper is incomplete.')

verify_document_helper(args.publish)
retained = assemble_payload(args.previous, args.publish, package, args.retain_component, args.finalize_staging)
if args.setup:
    if args.finalize_staging:
        if hashlib.sha256(args.setup.read_bytes()).digest() != hashlib.sha256((package / 'Setup.exe').read_bytes()).digest():
            raise SystemExit('Finalization cannot change the staged installer; use a fresh output.')
    else:
        shutil.copyfile(args.setup, package / 'Setup.exe')
if not (package / 'payload/updater/SovereignAI.Updater.exe').is_file():
    raise SystemExit('Fresh publish or an explicitly retained component must include the updater.')
verify_document_helper(package / 'payload')
if args.documents:
    for document in args.documents.glob('*.md'):
        shutil.copyfile(document, package / document.name)
for path in package.iterdir():
    if path.is_file() and path.suffix.lower() in {'.md', '.ps1', '.cmd'} and not path.name.startswith('RELEASE-NOTES'):
        path.write_text(path.read_text(encoding='utf-8-sig').replace(args.previous_version, args.version), encoding='utf-8')
notes = args.notes.read_text(encoding='utf-8-sig')
(package / ('RELEASE-NOTES-' + args.version.replace('.', '') + '.md')).write_text(notes, encoding='utf-8')
(args.output / 'release-notes.md').write_text(notes, encoding='utf-8')
inventory = create_inventory(package, args.nuget_cache)
files = []
for path in sorted(package.rglob('*')):
    if not path.is_file() or path == package / 'files.json':
        continue
    if path.name.lower() in {'.env', 'auth.json', 'credentials.json', 'transcript.json'} or path.suffix.lower() in {'.db', '.sqlite', '.sqlite3'}:
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
if archive.stat().st_size > 1_000_000_000:
    raise SystemExit('Package exceeds the verified updater size limit; no release receipt produced.')
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
Path(str(archive) + '.sha256').write_text(digest + '  ' + archive.name + '\n')
receipt = dict(files=len(files), bytes=archive.stat().st_size, sha256=digest, archive=str(archive), retained_components=retained,
               setup_sha256=hashlib.sha256((package / 'Setup.exe').read_bytes()).hexdigest(), fresh_setup=args.setup is not None)
receipt['component_count'] = len(inventory['components'])
receipt['component_license_metadata_missing'] = [item['name'] + '/' + item['version'] for item in inventory['components'] if item['license_status'] in ('metadata_unavailable', 'metadata_without_license')]
(args.output / 'package-receipt.json').write_text(json.dumps(receipt, indent=2))
print(json.dumps(receipt))
