"""Filesystem fixtures and command doubles; never targets the real installation."""
import contextlib
import json
import shlex
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import uninstall


class UninstallTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='Link-Uninstall-Test-')
        self.addCleanup(self.temp.cleanup)
        self.root = (Path(self.temp.name) / 'Link').resolve()
        self.units = (Path(self.temp.name) / 'units').resolve()
        self.units.mkdir()
        (self.root / 'deploy').mkdir(parents=True)
        (self.root / 'data').mkdir()
        (self.root / 'data' / 'identity').write_text('synthetic')
        (self.root / '.link-owned').touch()
        (self.root / 'deploy' / 'guard.nft').write_text('synthetic')
        self.unit = self.units / 'link-server.service'
        self.source = self.root / 'deploy' / self.unit.name
        self.source.write_text('ExecStart=' + shlex.quote((self.root / 'bin' / 'link-server').as_posix()) + '\n')
        # No elevated symlink creation needed on Windows; emulate only this unit's link.
        self.unit.write_text('synthetic unit link')
        self.calls = []

    @contextlib.contextmanager
    def owned_unit(self):
        original_link, original_resolve = Path.is_symlink, Path.resolve
        with patch.object(Path, 'is_symlink', lambda p: True if p == self.unit else original_link(p)), patch.object(Path, 'resolve', lambda p, *a, **kw: self.source if p == self.unit else original_resolve(p, *a, **kw)):
            yield

    def execute(self, *args, **kwargs):
        self.calls.append(args)
        if args[:2] == ('systemctl', 'show'):
            return subprocess.CompletedProcess(args, 0, 'inactive\n')
        if args[0] == 'ip':
            return subprocess.CompletedProcess(args, 1, '')
        return subprocess.CompletedProcess(args, 0, json.dumps({'nftables': [{'table': {'family': 'inet', 'name': 'link_guard'}}, {'table': {'family': 'inet', 'name': 'unrelated'}}]}))

    def test_foreign_unit_rejected_before_mutation(self):
        with self.assertRaisesRegex(RuntimeError, 'unowned unit'):
            uninstall.remove(self.root, self.units, execute=self.execute)
        self.assertEqual(self.calls, [])
        self.assertTrue(self.root.exists())

    def test_only_owned_services_and_guard_removed(self):
        # Linux unit paths are POSIX; preserve host separators for the cross-platform fixture.
        with self.owned_unit():
            uninstall.remove(self.root, self.units, execute=self.execute)
        self.assertFalse(self.root.exists())
        self.assertEqual([c for c in self.calls if c[:2] == ('systemctl', 'disable')], [('systemctl', 'disable', '--now', 'link-server.service')])
        self.assertEqual([c for c in self.calls if c[:2] == ('nft', 'delete')], [('nft', 'delete', 'table', 'inet', 'link_guard')])

    def test_keep_data_and_failure_retains_recovery(self):
        with self.owned_unit():
            def fail(*args, **kwargs):
                if args[:2] == ('systemctl', 'disable'):
                    raise RuntimeError('synthetic stop failure')
                return self.execute(*args, **kwargs)
            with self.assertRaisesRegex(RuntimeError, 'stop failure'):
                uninstall.remove(self.root, self.units, execute=fail)
            self.assertTrue((self.root / 'data' / 'identity').exists())
            uninstall.remove(self.root, self.units, keep_data=True, execute=self.execute)
        self.assertTrue((self.root / 'data' / 'identity').exists())
        self.assertFalse((self.root / 'deploy').exists())


if __name__ == '__main__':
    unittest.main()
