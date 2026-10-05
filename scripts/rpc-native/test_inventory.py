import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

SPEC = importlib.util.spec_from_file_location('inventory', Path(__file__).with_name('inventory.py'))
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

class InventoryTests(unittest.TestCase):
    def test_missing_or_invalid_control_hash_is_rejected(self):
        listing = '# rpc-bench fingerprint v2\n# listing\nCURRENT\tf\t20\t123.0\t644\t1:1\t\n# control-file-hashes\n'
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / 'fingerprint'
            for suffix, accepted in (('a' * 64 + '  CURRENT\n', True), ('', False),
                                     ('  CURRENT\n', False), ('a' * 64 + '  IDENTITY\n', False)):
                with self.subTest(suffix=suffix):
                    target.write_text(listing + suffix)
                    if accepted:
                        MODULE.complete_fingerprint(target)
                    else:
                        with self.assertRaises(ValueError):
                            MODULE.complete_fingerprint(target)

    def test_timeout_retains_hold_and_never_signals_wrapper(self):
        child = Mock()
        child.communicate.side_effect = subprocess.TimeoutExpired('find', 1)
        with tempfile.TemporaryDirectory() as folder, patch.object(MODULE, 'PRIVATE', Path(folder)), patch.object(MODULE.subprocess, 'Popen', return_value=child):
            with self.assertRaises(subprocess.TimeoutExpired):
                MODULE.command(['find'], timeout=1)
            record = json.loads((Path(folder) / 'OWNERSHIP-HOLD.json').read_text())
            self.assertEqual(record['status'], 'UNKNOWN_CHILD_IO_NO_NEXT_DISPATCH')
            child.kill.assert_not_called()
            child.terminate.assert_not_called()

if __name__ == '__main__':
    unittest.main()
