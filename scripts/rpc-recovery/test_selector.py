import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import select_recovery as selector
import restore as recovery


class SelectorTests(unittest.TestCase):
    def test_selects_exact_live_or_empty_without_process_operations(self):
        for entries, selected in (([], 'absent'), ([{'ID':recovery.CID,'State':'running'}], 'live')):
            with self.subTest(selected=selected):
                live, absent = Mock(), Mock()
                selector.invoke(entries, live, absent)
                (live if selected == 'live' else absent).assert_called_once_with()
                (absent if selected == 'live' else live).assert_not_called()

    def test_foreign_stopped_or_multiple_containers_hold(self):
        for entries in ([{'ID':'a'*64,'State':'running'}], [{'ID':recovery.CID,'State':'exited'}],
                        [{'ID':recovery.CID,'State':'running'}, {'ID':'a'*64,'State':'running'}]):
            with self.subTest(entries=entries):
                live, absent = Mock(), Mock()
                with self.assertRaises(ValueError):
                    selector.invoke(entries, live, absent)
                live.assert_not_called()
                absent.assert_not_called()

    def test_engine_failure_never_falls_back(self):
        for entries in ([], [{'ID':recovery.CID,'State':'running'}]):
            with self.subTest(entries=entries):
                live, absent = Mock(side_effect=RuntimeError('partial')), Mock(side_effect=RuntimeError('partial'))
                with self.assertRaisesRegex(RuntimeError, 'partial'):
                    selector.invoke(entries, live, absent)
                self.assertEqual(live.call_count + absent.call_count, 1)

    def test_wrong_runner_stops_before_resource_reads(self):
        for runner in ('', 'other'):
            with self.subTest(runner=runner), patch.dict(os.environ, {'RUNNER_NAME':runner}), patch.object(recovery, 'output') as command:
                with self.assertRaisesRegex(ValueError, 'EXACT_RUNNER_REQUIRED'):
                    selector.main()
                command.assert_not_called()

    def test_all_tool_and_version_references_hold_even_before_live_stop(self):
        with tempfile.TemporaryDirectory() as temporary:
            proc = Path(temporary)
            child = proc/'101'
            child.mkdir()
            (child/'fd').mkdir()
            for name, value in [('environ',b''),('cgroup',b'0::/system.slice/containerd.service'),('stat',b'101 synthetic')]:
                (child/name).write_bytes(value)
            for cid in (recovery.CID, recovery.TOOL_CID, recovery.VERSION_CID):
                (child/'cmdline').write_bytes(b'synthetic-shim\0-id\0'+cid.encode())
                for allow_node in (False,True):
                    blocked = cid != recovery.CID or not allow_node
                    with self.subTest(cid=cid,allow_node=allow_node), patch.object(recovery, 'Path', return_value=proc), patch.object(recovery.os, 'readlink', return_value='/unrelated'), patch.object(recovery.os, 'getpid', return_value=999), patch.object(recovery, 'write'):
                        if blocked:
                            with self.assertRaisesRegex(ValueError, 'OWNED_HELPERS_NOT_QUIESCENT'):
                                recovery.scan_helpers(allow_node)
                        else:
                            recovery.scan_helpers(allow_node)


if __name__ == '__main__':
    unittest.main()
