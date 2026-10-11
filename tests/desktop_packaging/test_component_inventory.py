from pathlib import Path
import json
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from desktop_component_inventory import create_inventory, nuget_notices


class ComponentInventoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.package = self.root / 'package'
        self.cache = self.root / 'cache'
        self.write(self.package / 'payload/app.deps.json', json.dumps({'libraries': {
            'Library/1.0.0': {'type': 'package', 'sha512': 'upstream-package-hash'},
            'App/1.2.3': {'type': 'project'}}}))
        self.write(self.cache / 'library/1.0.0/library.nuspec', '<package><metadata><license type="file">legal/LICENSE.txt</license></metadata></package>')
        self.write(self.cache / 'library/1.0.0/legal/LICENSE.txt', 'upstream license bytes')
        self.write(self.cache / 'library/1.0.0/NOTICE.txt', 'upstream notice')

    def tearDown(self): self.temp.cleanup()
    def write(self, path, text): path.parent.mkdir(parents=True, exist_ok=True); path.write_text(text, encoding='utf-8')

    def test_exact_dependency_licenses_and_metadata_are_retained(self):
        result = create_inventory(self.package, self.cache)
        library = next(item for item in result['components'] if item['name'] == 'Library')
        self.assertEqual(library['license_status'], 'declared')
        self.assertEqual(len(library['license_files']), 2)
        self.assertEqual((self.package / 'licenses/nuget/library/1.0.0/legal/LICENSE.txt').read_text(), 'upstream license bytes')
        self.assertEqual(library['package_sha512'], 'upstream-package-hash')
        encoded = (self.package / 'component-inventory.json').read_text()
        self.assertNotIn(str(self.root), encoded)
        first = encoded
        create_inventory(self.package, self.cache)
        self.assertEqual(first, (self.package / 'component-inventory.json').read_text())

    def test_python_locations_are_distinct_with_one_component_identity(self):
        for location in ('desk', 'desk-runtime/desk'):
            self.write(self.package / f'payload/{location}/deps/example-2.0.dist-info/METADATA', 'Name: example\nVersion: 2.0\nLicense-Expression: MIT\n')
            self.write(self.package / f'payload/{location}/deps/example-2.0.dist-info/licenses/LICENSE', 'MIT license')
        item = next(item for item in create_inventory(self.package, self.cache)['components'] if item['ecosystem'] == 'pypi')
        self.assertEqual(len(item['metadata']), 2)
        self.assertEqual(len(item['license_files']), 2)

    def test_absent_license_is_reported_and_not_invented(self):
        result = nuget_notices(self.package, self.cache, 'Absent', '1.0')
        self.assertEqual(result, {'license_status': 'metadata_unavailable', 'license_files': []})

    def test_legacy_license_url_is_retained_without_inventing_license_text(self):
        self.write(self.cache / 'legacy/1.0/legacy.nuspec', '<package><metadata><licenseUrl>https://example.com/license</licenseUrl></metadata></package>')
        result = nuget_notices(self.package, self.cache, 'legacy', '1.0')
        self.assertEqual(result['license_declaration'], {'type': 'url', 'value': 'https://example.com/license'})
        self.assertEqual(result['license_files'], [])
        self.assertIn('no license text', result['license_note'])

    def test_license_escape_is_refused(self):
        self.write(self.cache / 'library/1.0.0/library.nuspec', '<package><metadata><license type="file">../../private.txt</license></metadata></package>')
        self.write(self.cache / 'private.txt', 'must not copy')
        with self.assertRaises(ValueError): create_inventory(self.package, self.cache)
        self.assertFalse((self.package / 'licenses').exists())

    def test_missing_declared_file_is_refused(self):
        (self.cache / 'library/1.0.0/legal/LICENSE.txt').unlink()
        with self.assertRaises(ValueError): create_inventory(self.package, self.cache)


if __name__ == '__main__': unittest.main()
