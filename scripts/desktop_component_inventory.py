"""Inventory staged dependency metadata and carry its upstream license notices.

This is a component inventory, not a claim that every third-party license has
been legally reviewed. Unknown metadata remains explicit in the generated file.
No package source, developer path or credentials enter the inventory.
"""
from pathlib import Path
from email.parser import Parser
import hashlib
import json
import re
import shutil
import xml.etree.ElementTree as ET


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def safe_segment(value):
    if not re.fullmatch(r'[A-Za-z0-9_.+-]+', value) or value in ('.', '..'):
        raise ValueError('Invalid component identity.')
    return value


def within(root, path):
    root, path = Path(root).resolve(), Path(path)
    if path.is_symlink() or path.is_junction() or not path.resolve().is_relative_to(root):
        raise ValueError('License path escapes its package.')
    return path


def nuget_notices(package, cache, name, version):
    """Resolve only the exact package and relative upstream license paths."""
    name, version = safe_segment(name), safe_segment(version)
    directory = Path(cache) / name.lower() / version.lower()
    metadata = directory / (name.lower() + '.nuspec')
    if not metadata.is_file():
        return {'license_status': 'metadata_unavailable', 'license_files': []}
    within(cache, directory)
    tree = ET.parse(metadata).getroot()
    fields = {element.tag.rsplit('}', 1)[-1]: element for element in tree.iter()}
    license_node = fields.get('license')
    declared = None if license_node is None else (license_node.text or '').strip()
    kind = None if license_node is None else license_node.attrib.get('type')
    legacy_url = fields.get('licenseUrl')
    if not declared and legacy_url is not None and (legacy_url.text or '').strip():
        declared = legacy_url.text.strip()
        kind = 'url'
    sources = set()
    if declared and kind == 'file':
        target = within(directory, directory / declared.replace('\\', '/'))
        if not target.is_file():
            raise ValueError('Declared NuGet license file is missing: ' + name + '/' + version)
        sources.add(target)
    for child in directory.iterdir():
        if child.is_file() and re.match(r'(?i)^(licen[cs]e|notice|third[-_ ]?party)', child.name):
            sources.add(within(directory, child))
    copied = []
    for source in sorted(sources):
        relative = source.relative_to(directory)
        destination = package / 'licenses' / 'nuget' / name.lower() / version / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
        copied.append({'path': destination.relative_to(package).as_posix(), 'sha256': sha256(destination)})
    result = {'license_status': 'declared' if declared or copied else 'metadata_without_license', 'license_files': copied}
    if declared:
        result['license_declaration'] = {'type': kind or 'unknown', 'value': declared}
        if kind == 'url':
            result['license_note'] = 'Upstream package provides this license URL; no license text was bundled by this package.'
    return result


def create_inventory(package, nuget_cache):
    package = Path(package)
    components = {}
    for metadata in sorted((package / 'payload').rglob('*.deps.json')):
        within(package, metadata)
        for identity, info in json.loads(metadata.read_text(encoding='utf-8-sig')).get('libraries', {}).items():
            if '/' not in identity:
                raise ValueError('Dependency identity is missing its version.')
            name, version = identity.rsplit('/', 1)
            category = info.get('type', 'unknown')
            if category not in ('package', 'runtimepack', 'project', 'reference'):
                category = 'unknown'
            key = ('nuget' if category in ('package', 'runtimepack') else category, name, version)
            item = components.setdefault(key, {'ecosystem': key[0], 'name': name, 'version': version, 'metadata': [], 'license_files': []})
            item['metadata'].append({'path': metadata.relative_to(package).as_posix(), 'sha256': sha256(metadata)})
            if info.get('sha512'):
                item['package_sha512'] = info['sha512']
    for metadata in sorted((package / 'payload').rglob('*.dist-info/METADATA')):
        within(package, metadata)
        info = Parser().parsestr(metadata.read_text(encoding='utf-8', errors='strict'))
        name, version = info.get('Name'), info.get('Version')
        if not name or not version:
            raise ValueError('Python dependency metadata is missing its identity.')
        key = ('pypi', name, version)
        item = components.setdefault(key, {'ecosystem': 'pypi', 'name': name, 'version': version, 'metadata': [], 'license_files': []})
        item['metadata'].append({'path': metadata.relative_to(package).as_posix(), 'sha256': sha256(metadata)})
        declared = info.get('License-Expression') or info.get('License')
        if declared and len(declared) <= 200:
            item['license_declaration'] = {'type': 'metadata', 'value': declared}
        for source in sorted(metadata.parent.rglob('*')):
            if source.is_file() and re.match(r'(?i)^(licen[cs]e|copying|notice)', source.name):
                within(package, source)
                item['license_files'].append({'path': source.relative_to(package).as_posix(), 'sha256': sha256(source)})
        item['license_status'] = 'declared' if item.get('license_declaration') or item['license_files'] else 'metadata_without_license'
    for key, item in components.items():
        if item['ecosystem'] == 'nuget':
            package_name = item['name'].removeprefix('runtimepack.')
            item.update(nuget_notices(package, nuget_cache, package_name, item['version']))
        elif item['ecosystem'] in ('project', 'reference', 'unknown'):
            item['license_status'] = 'application_or_reference'
    existing_notices = []
    for source in sorted(package.rglob('*')):
        if source.is_file() and re.match(r'(?i)^(licen[cs]e|copying|notice|third[-_ ]?party|.*[-_]notice\.)', source.name):
            within(package, source)
            existing_notices.append({'path': source.relative_to(package).as_posix(), 'sha256': sha256(source)})
    result = {'format': 'sovereign-component-inventory-v1',
              'scope': 'Staged .NET dependency graphs, Python distribution metadata, and retained notice files. Not a legal review or proof of unrecorded native-component origins.',
              'components': [components[key] for key in sorted(components)], 'notice_files': existing_notices}
    destination = package / 'component-inventory.json'
    destination.write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    return result
