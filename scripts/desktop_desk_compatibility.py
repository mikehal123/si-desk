"""Bind release compatibility metadata to the exact staged worker sources."""
import argparse
import ast
import json
import re
from pathlib import Path


def constant(path, name):
    values = [node.value for node in ast.parse(path.read_text(encoding='utf-8-sig')).body
              if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == name for t in node.targets)]
    if len(values) != 1:
        raise ValueError('Missing or ambiguous worker constant: ' + name)
    value = ast.literal_eval(values[0])
    if type(value) is not int or value < 1:
        raise ValueError('Invalid worker constant: ' + name)
    return value


def expected(publish, version):
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Invalid release version.')
    runtime = Path(publish) / 'desk-runtime' / 'desk'
    minimum = constant(runtime / 'store/service.py', 'READ_VERSION')
    write = constant(runtime / 'store/service.py', 'WRITE_VERSION')
    protocol = constant(runtime / 'contracts/desk_contracts.py', 'PROTOCOL_VERSION')
    if minimum > write:
        raise ValueError('Worker schema range is inconsistent.')
    return dict(format='sovereign-desk-compatibility-v1', app_version=version,
                protocol_version=protocol, min_read_schema=minimum,
                max_read_schema=write, write_schema=write)


def verify(publish, version):
    path = Path(publish) / 'desk-compatibility.json'
    if not path.is_file() or path.is_symlink() or path.stat().st_size > 65536:
        raise ValueError('A bounded, fresh desk compatibility manifest is required.')
    actual = json.loads(path.read_text(encoding='utf-8-sig'))
    target = expected(publish, version)
    if actual != target or any(type(actual[k]) is not int for k in ('protocol_version', 'min_read_schema', 'max_read_schema', 'write_schema')):
        raise ValueError('Desk compatibility manifest differs from staged worker/version.')
    return actual


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--publish', type=Path, required=True)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    path = args.publish / 'desk-compatibility.json'
    if path.exists():
        raise SystemExit('Use a fresh publish directory; compatibility metadata already exists.')
    path.write_text(json.dumps(expected(args.publish, args.version), indent=2) + '\n', encoding='utf-8')
    verify(args.publish, args.version)
    print('Desk compatibility metadata matches the staged worker.')
