# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).with_name('spin-diagnostic')))
import collect_diagnostic as module


class CollectionTests(unittest.TestCase):
    def test_bounded_archive_is_attempted_and_any_product_failure_remains_failure(self):
        for decode_failed, archive_code in ((False, 0), (True, 0), (False, 1)):
            with self.subTest(decode_failed=decode_failed, archive_code=archive_code), \
                    tempfile.TemporaryDirectory() as temp:
                temporary = Path(temp).resolve()
                root = temporary / 'spin-diagnostic-123-1'
                (root / 'outputs' / 'only-run').mkdir(parents=True)
                (root / 'source-map').mkdir()
                for name in ('identity-map.jsonl', 'corpus-identity.json'):
                    (root / 'source-map' / name).write_text('{}')
                with patch.dict(os.environ, {'RUNNER_TEMP': str(temporary), 'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '1'}), \
                        patch.object(module, 'decode_capture', return_value={'status': 'REQUIRES_CAPABILITY_AND_WINDOW_REVIEW'},
                                     side_effect=RuntimeError('PRIVATE') if decode_failed else None), \
                        patch.object(module, 'export_scheduler', return_value={'status': 'PROJECTION_COMPLETE_REQUIRES_CAPABILITY_REVIEW'}), \
                        patch.object(module, 'project', return_value=([], {'status': 'VALID'})), \
                        patch.object(module.subprocess, 'run', return_value=subprocess.CompletedProcess([], archive_code)) as archive, \
                        redirect_stdout(io.StringIO()) as public:
                    code = module.main()
                self.assertEqual(code, 1 if decode_failed or archive_code else 0)
                archive.assert_called_once()
                self.assertEqual(archive.call_args.kwargs, {'check': False, 'timeout': 600})
                status = json.loads((temporary / 'spin-public-123-1' / 'collection-status.json').read_text())
                self.assertEqual(status['steps']['encrypted_archive']['completed'], archive_code == 0)
                self.assertEqual(status['steps']['scheduler_decode']['completed'], not decode_failed)
                self.assertNotIn('PRIVATE', public.getvalue())
                self.assertTrue(root.is_dir())


if __name__ == '__main__':
    unittest.main()
