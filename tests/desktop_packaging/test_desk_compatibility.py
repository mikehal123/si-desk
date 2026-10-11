from pathlib import Path
import json
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from desktop_desk_compatibility import expected, verify


class DeskCompatibilityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.service = self.root / 'desk-runtime/desk/store/service.py'
        self.contract = self.root / 'desk-runtime/desk/contracts/desk_contracts.py'
        self.service.parent.mkdir(parents=True)
        self.contract.parent.mkdir(parents=True)
        self.service.write_text('READ_VERSION = 1\nWRITE_VERSION = 2\n', encoding='utf-8')
        self.contract.write_text('PROTOCOL_VERSION = 1\n', encoding='utf-8')

    def tearDown(self):
        self.temp.cleanup()

    def save(self, value):
        (self.root / 'desk-compatibility.json').write_text(json.dumps(value), encoding='utf-8')

    def test_metadata_follows_actual_staged_worker(self):
        self.save(expected(self.root, '0.3.35'))
        self.assertEqual(verify(self.root, '0.3.35')['write_schema'], 2)
        self.service.write_text('READ_VERSION = 2\nWRITE_VERSION = 3\n', encoding='utf-8')
        with self.assertRaises(ValueError): verify(self.root, '0.3.35')
        self.save(expected(self.root, '0.3.35'))
        self.assertEqual(verify(self.root, '0.3.35')['min_read_schema'], 2)

    def test_old_app_version_and_boolean_schema_are_refused(self):
        value = expected(self.root, '0.3.34')
        self.save(value)
        with self.assertRaises(ValueError): verify(self.root, '0.3.35')
        value = expected(self.root, '0.3.35')
        value['protocol_version'] = True
        self.save(value)
        with self.assertRaises(ValueError): verify(self.root, '0.3.35')

    def test_missing_metadata_is_refused(self):
        with self.assertRaises(ValueError): verify(self.root, '0.3.35')

    def test_ambiguous_or_dynamic_constants_are_not_executed(self):
        for source in ('READ_VERSION=1\nWRITE_VERSION=2\nWRITE_VERSION=3\n',
                       'READ_VERSION=1\nWRITE_VERSION=int("2")\n'):
            self.service.write_text(source, encoding='utf-8')
            with self.assertRaises((ValueError, TypeError)): expected(self.root, '0.3.35')

    def test_invalid_schema_range_is_refused(self):
        self.service.write_text('READ_VERSION=3\nWRITE_VERSION=2\n', encoding='utf-8')
        with self.assertRaises(ValueError): expected(self.root, '0.3.35')


if __name__ == '__main__': unittest.main()
