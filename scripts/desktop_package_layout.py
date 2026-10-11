"""Fresh release payloads with explicitly named, hash-checked retained components."""
from pathlib import Path, PureWindowsPath
import hashlib
import json
import shutil


def relative(value):
    value = value.replace('\\', '/')
    path = PureWindowsPath(value)
    if not value or path.is_absolute() or path.drive or any(p in ('', '.', '..') for p in value.split('/')) or ':' in value:
        raise ValueError('Invalid package relative path: ' + value)
    return value


def files(root):
    root = Path(root)
    if root.is_symlink() or root.is_junction():
        raise ValueError('Package links are not permitted.')
    result = {}
    for path in root.rglob('*'):
        if path.is_symlink() or path.is_junction():
            raise ValueError('Package links are not permitted.')
        if path.is_file():
            name = relative(path.relative_to(root).as_posix())
            key = name.casefold()
            if key in result:
                raise ValueError('Duplicate package path: ' + name)
            result[key] = (name, path)
    return result


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def verify_inventory(root, rows, excluded=()):
    actual = files(root)
    listed = set()
    for row in rows:
        name = relative(row['Path'])
        key = name.casefold()
        if key in listed or key not in actual:
            raise ValueError('Invalid or duplicate component inventory path: ' + name)
        listed.add(key)
        path = actual[key][1]
        if path.stat().st_size != row['Bytes'] or digest(path) != row['Sha256']:
            raise ValueError('Component bytes do not match the embedded inventory: ' + name)
    if set(actual) - {relative(name).casefold() for name in excluded} != listed:
        raise ValueError('Unexpected or missing component files.')


def payload_plan(previous, publish, retained):
    """No implicit carry-forward. Retained paths may not overlap fresh publish paths."""
    previous, publish = Path(previous), Path(publish)
    plan = files(publish)
    manifest = {}
    if retained:
        for row in json.loads((previous / 'files.json').read_text(encoding='utf-8-sig')):
            key = relative(row['path']).casefold()
            if key in manifest:
                raise ValueError('Duplicate prior manifest path.')
            manifest[key] = row
    retained_receipts = []
    old_files = files(previous / 'payload') if retained else {}
    for requested in retained:
        name = relative(requested)
        prefix = name.casefold()
        selected = {key: item for key, item in old_files.items() if key == prefix or key.startswith(prefix + '/')}
        if not selected:
            raise ValueError('Retained component is missing: ' + name)
        for key, (file_name, path) in selected.items():
            if key in plan:
                raise ValueError('Retained component overlaps publish or another retained path: ' + file_name)
            row = manifest.get('payload/' + key)
            if row is None or path.stat().st_size != row['bytes'] or digest(path) != row['sha256']:
                raise ValueError('Retained bytes do not match the prior package manifest: ' + file_name)
            plan[key] = (file_name, path)
        retained_receipts.append({'path': name, 'files': len(selected)})
    return plan, retained_receipts


def assemble_payload(previous, publish, package, retained, finalize=False):
    previous, publish, package = Path(previous), Path(publish), Path(package)
    plan, receipts = payload_plan(previous, publish, retained)
    if finalize:
        actual = files(package / 'payload')
        if set(actual) != set(plan):
            raise ValueError('Staged payload differs from the publish/retained plan; use a fresh output.')
        for key, (_, source) in plan.items():
            if digest(actual[key][1]) != digest(source):
                raise ValueError('Finalization cannot change staged application bytes; use a fresh output.')
    else:
        if package.exists():
            raise ValueError('Use a fresh package output.')
        # Installer/documentation template only. Never clone its old application tree.
        template = files(previous)
        package.mkdir(parents=True)
        for _, (name, source) in template.items():
            if name.casefold() == 'files.json' or name.casefold().startswith('payload/'):
                continue
            target = package / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
        for _, (name, source) in plan.items():
            target = package / 'payload' / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
    return receipts
