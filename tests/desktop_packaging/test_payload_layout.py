from pathlib import Path
import hashlib
import ast
import json
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from desktop_package_layout import assemble_payload, relative, verify_inventory


class PayloadLayoutTests(unittest.TestCase):
    def test_packaging_entry_point_parses(self):
        ast.parse((Path(__file__).resolve().parents[2] / 'scripts/Package-DesktopRelease.py').read_text())
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.old = self.root / 'old'; self.new = self.root / 'publish'; self.out = self.root / 'package'
        self.write(self.old / 'Setup.exe', b'installer')
        self.write(self.old / 'payload/removed.dll', b'obsolete')
        self.write(self.old / 'payload/updater/helper.exe', b'pinned helper')
        self.write(self.new / 'app.exe', b'fresh app')
        manifest = [{'path': str(p.relative_to(self.old)), 'bytes': p.stat().st_size,
                     'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in self.old.rglob('*') if p.is_file()]
        (self.old / 'files.json').write_text(json.dumps(manifest))
    def tearDown(self): self.temp.cleanup()
    def write(self, path, data): path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(data)
    def test_fresh_payload_drops_stale_files_and_records_explicit_retention(self):
        receipt = assemble_payload(self.old, self.new, self.out, ['updater'])
        self.assertFalse((self.out / 'payload/removed.dll').exists())
        self.assertEqual((self.out / 'payload/app.exe').read_bytes(), b'fresh app')
        self.assertEqual((self.out / 'payload/updater/helper.exe').read_bytes(), b'pinned helper')
        self.assertEqual(receipt, [{'path': 'updater', 'files': 1}])
        self.assertFalse((self.out / 'files.json').exists())
    def test_retention_is_never_implicit(self):
        assemble_payload(self.old, self.new, self.out, [])
        self.assertFalse((self.out / 'payload/updater').exists())
    def test_tampered_retained_component_refused_before_output(self):
        (self.old / 'payload/updater/helper.exe').write_bytes(b'changed')
        with self.assertRaises(ValueError): assemble_payload(self.old, self.new, self.out, ['updater'])
        self.assertFalse(self.out.exists())
    def test_overlap_missing_and_duplicate_refused(self):
        for retain in [['missing'], ['updater', 'updater']]:
            with self.assertRaises(ValueError): assemble_payload(self.old, self.new, self.out, retain)
        self.write(self.new / 'updater/helper.exe', b'new helper')
        with self.assertRaises(ValueError): assemble_payload(self.old, self.new, self.out, ['updater'])
    def test_finalization_rejects_extra_or_changed_payload(self):
        assemble_payload(self.old, self.new, self.out, ['updater'])
        assemble_payload(self.old, self.new, self.out, ['updater'], True)
        self.write(self.out / 'payload/extra.dll', b'stale')
        with self.assertRaises(ValueError): assemble_payload(self.old, self.new, self.out, ['updater'], True)
        (self.out / 'payload/extra.dll').unlink()
        (self.out / 'payload/app.exe').write_bytes(b'changed')
        with self.assertRaises(ValueError): assemble_payload(self.old, self.new, self.out, ['updater'], True)
    def test_escape_and_absolute_retention_refused(self):
        for name in ['../secret', '/absolute', 'C:/absolute', 'C:relative', 'updater/../app', 'file:stream', 'updater//helper']:
            with self.subTest(name=name), self.assertRaises(ValueError): relative(name)
    def test_component_inventory_rejects_stale_file_and_duplicate(self):
        rows = [{'Path': 'app.exe', 'Bytes': 9, 'Sha256': hashlib.sha256(b'fresh app').hexdigest()}]
        verify_inventory(self.new, rows)
        with self.assertRaises(ValueError): verify_inventory(self.new, rows + rows)
        self.write(self.new / 'old.py', b'injected')
        with self.assertRaises(ValueError): verify_inventory(self.new, rows)

if __name__ == '__main__': unittest.main()
