# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import hashlib
import json
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).with_name('spin-diagnostic')))
import export_target_numeric as ex

CANARY = 'FOREIGN_SECRET'
SWITCH = '900 [003] 123.200000001: sched:sched_switch: prev_comm=pid=42 prev_pid=500 prev_prio=120 prev_state=S ==> next_comm=foreign next_pid=501 next_prio=120\n'
TARGET = '900 [003] 123.300000001: sched:sched_switch: prev_comm=LOST pid=42 prev_pid=42 prev_prio=120 prev_state=R+ ==> next_comm=foreign next_pid=500 next_prio=120\n'
WAKE = '500 [004] 123.400000001: sched:sched_wakeup: comm=pid=42 pid=42 prio=120 target_cpu=003\n'
NEW = '500 [004] 123.500000001: sched:sched_wakeup_new: comm=FOREIGN_SECRET pid=43 prio=120 target_cpu=003\n'


def clock(ns):
    return {'monotonic_before_ns': ns, 'realtime_ns': 1700000000000000000 + ns, 'monotonic_after_ns': ns + 10, 'secret': CANARY}


def tid(value, start=100):
    return {'tid': value, 'tgid': 42, 'start_ticks': start, 'nspid': [value, value - 40], 'secret': CANARY}


def snapshot(ns, threads):
    return {'clock': clock(ns), 'leader': tid(42), 'threads': threads, 'command': CANARY}


class ProjectionTests(unittest.TestCase):
    def test_source_limits_match_collector_and_reject_boundary_overflow(self):
        expected = {'sched.data': ex.collector.RECORD_LIMIT - 1,
                    'sched.script.txt': ex.collector.SCRIPT_LIMIT - 1,
                    'perf-record.log': ex.collector.RECORD_FILE_LIMIT,
                    'perf-script.log': ex.collector.SCRIPT_LIMIT}
        for name, limit in expected.items():
            self.assertEqual(ex.SOURCE_LIMITS[name], limit)
            for size in (limit, limit + 1):
                with self.subTest(name=name, size=size):
                    path = Mock()
                    path.lstat.return_value = Mock(st_mode=stat.S_IFREG, st_size=size,
                                                  st_dev=1, st_ino=2, st_mtime_ns=3)
                    if size == limit:
                        self.assertEqual(ex.file_identity(path, limit), (1, 2, size, 3))
                    else:
                        with self.assertRaisesRegex(ex.ExportError, 'SIZE_INVALID'):
                            ex.file_identity(path, limit)

    def test_projection_accepts_exact_cap_and_rejects_next_byte_before_write(self):
        self.prepare()
        measured = self.run_export('reference')['projection']['bytes']
        for cap in (measured, measured - 1):
            name = 'cap-' + str(cap)
            with self.subTest(cap=cap), patch.object(ex.collector, 'PROJECTION_LIMIT', cap):
                if cap == measured:
                    self.assertEqual(self.run_export(name)['projection']['bytes'], cap)
                else:
                    with self.assertRaisesRegex(ex.ExportError, 'PUBLIC_PROJECTION_SIZE_LIMIT'):
                        self.run_export(name)
                    self.assertLessEqual((self.root / name / 'target-events.jsonl').stat().st_size, cap)
                    self.assertFalse((self.root / name / 'projection.json').exists())

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.private = self.root / 'private'
        self.private.mkdir()

    def prepare(self, script=SWITCH + TARGET + WAKE + NEW, generations=False):
        initial = snapshot(123000000000, [tid(42), tid(43, 200)])
        final = snapshot(123900000000, [tid(42), tid(43, 201 if generations else 200)])
        started = {'initial_target': initial, 'secret': CANARY}
        result = {'status': 'RECORDED_REQUIRES_LINUX_VALIDATION', 'replay_completed': True,
                  'record_returncode': -2, 'owned_stop': {'alive_before_stop': True, 'sigint_sent': True},
                  'clock_id': 'CLOCK_MONOTONIC', 'boot_id': '01234567-89ab-cdef-0123-456789abcdef', 'clock_ticks_per_second': 100,
                  'enable_before': clock(123100000000), 'enable_ack': clock(123150000000),
                  'stop_requested': clock(123800000000), 'stopped': clock(123850000000),
                  'snapshots': [initial], 'final_target': final, 'error': CANARY, 'command': [CANARY]}
        summary = ex.collector.parse_script(script.splitlines(True))
        bad = summary['lost_records'] or summary['malformed_lines'] or not summary['events']['sched_switch']
        summary['status'] = 'INVALID' if bad else 'REQUIRES_CAPABILITY_AND_WINDOW_REVIEW'
        for name, value in [('started.json', started), ('result.json', result), ('decode.json', summary), ('decoder-status.json', {'status': 'EXITED', 'returncode': 0, 'command': [CANARY]})]:
            (self.private / name).write_text(json.dumps(value), encoding='utf-8')
        for name, data in [('sched.script.txt', script.encode()), ('sched.data', CANARY.encode()), ('perf-record.log', CANARY.encode()), ('perf-script.log', b'')]:
            (self.private / name).write_bytes(data)

    def run_export(self, name='public'):
        # Fixtures are not private Linux capture; isolate only the POSIX access gate.
        with patch.object(ex, 'validate_private_directory'):
            return ex.export(self.private, self.root / name)

    def test_comm_cannot_invent_target_and_no_foreign_text_or_ids_are_exported(self):
        self.prepare()
        manifest = self.run_export()
        text = '\n'.join(path.read_text() for path in (self.root / 'public').iterdir())
        for forbidden in (CANARY, 'LOST pid=42', 'prev_comm', 'next_comm', 'emitter_tid', 'FOREIGN_SECRET', '"mentioned_tids"'):
            self.assertNotIn(forbidden, text)
        rows = [json.loads(line) for line in (self.root / 'public/target-events.jsonl').read_text().splitlines()]
        self.assertEqual([42, 42, 43], [row['target_tid'] for row in rows])
        self.assertEqual([0, 2, 2], [row['direction'] for row in rows])
        self.assertTrue(all(type(value) is int for row in rows for value in row.values()))
        self.assertEqual(1, rows[0]['preempted'])
        self.assertEqual(1, manifest['projection_counts']['dropped_source_events'])
        self.assertFalse(manifest['identity_certified'])
        self.assertFalse(manifest['thread_map_complete'])
        self.assertEqual(2, manifest['global_stats_before_filtering']['events']['sched_switch'])
        self.assertEqual(0, manifest['global_stats_before_filtering']['lost_records'])
        self.assertEqual(hashlib.sha256((self.private / 'sched.data').read_bytes()).hexdigest(), manifest['source_files']['sched.data']['sha256'])
        self.assertEqual(hashlib.sha256((self.root / 'public/target-events.jsonl').read_bytes()).hexdigest(), manifest['projection']['sha256'])

    def test_global_loss_and_malformed_are_retained_without_raw_text(self):
        for extra, counter in [('CPU 3 lost 17 events ' + CANARY + '\n', 'lost_records'), ('PERF_RECORD_LOST ' + CANARY + '\n', 'lost_records_unparsed'), ('bad ' + CANARY + '\n', 'malformed_lines')]:
            with self.subTest(counter=counter):
                self.prepare(TARGET + extra)
                manifest = self.run_export(counter)
                self.assertEqual('INVALID_SOURCE_QUALITY', manifest['status'])
                self.assertEqual(1, manifest['global_stats_before_filtering'][counter])
                text = (self.root / counter / 'projection.json').read_text()
                self.assertNotIn(CANARY, text)

    def test_reused_and_single_observed_ids_are_excluded(self):
        self.prepare(generations=True)
        manifest = self.run_export('reused')
        self.assertEqual(1, manifest['projection_counts']['reused_tid_references'])
        self.assertEqual([42], [item['tid'] for item in manifest['eligible_observed_ids']])
        self.prepare()
        path = self.private / 'result.json'
        result = json.loads(path.read_text())
        result['final_target']['threads'] = [tid(42)]
        path.write_text(json.dumps(result))
        manifest = self.run_export('single')
        self.assertEqual(1, manifest['projection_counts']['single_observation_references'])

    def test_unknown_wakeup_is_not_target_and_outside_observation_is_dropped(self):
        self.prepare(TARGET + NEW.replace('pid=43', 'pid=999'))
        manifest = self.run_export('unknown')
        self.assertEqual(2, manifest['projection_counts']['unknown_tid_references'])
        self.prepare()
        path = self.private / 'result.json'
        result = json.loads(path.read_text())
        result['snapshots'].append(snapshot(123450000000, [tid(42), tid(43, 200)]))
        result['final_target']['threads'] = [tid(42)]
        path.write_text(json.dumps(result))
        manifest = self.run_export('interval')
        self.assertEqual(1, manifest['projection_counts']['outside_observed_interval_references'])

    def test_unknown_capture_unowned_sigint_stats_mismatch_and_invalid_clock_fail(self):
        changes = [('status', 'UNKNOWN_RETAIN_OWNERSHIP'), ('owned_stop', {'sigint_sent': False}), ('clock_id', CANARY), ('boot_id', CANARY)]
        for key, value in changes:
            with self.subTest(key=key):
                self.prepare()
                path = self.private / 'result.json'
                result = json.loads(path.read_text())
                result[key] = value
                path.write_text(json.dumps(result))
                with self.assertRaises(ex.ExportError):
                    self.run_export()
                self.assertFalse((self.root / 'public').exists())
        self.prepare()
        path = self.private / 'decode.json'
        value = json.loads(path.read_text()); value['events']['sched_switch'] += 1
        path.write_text(json.dumps(value))
        with self.assertRaisesRegex(ex.ExportError, 'STATS_MISMATCH'):
            self.run_export()

    def test_no_overwrite_nested_output_or_malformed_numeric_metadata(self):
        self.prepare()
        self.run_export()
        with self.assertRaisesRegex(ex.ExportError, 'NEW_SEPARATE'):
            self.run_export()
        with patch.object(ex, 'validate_private_directory'), self.assertRaises(ex.ExportError):
            ex.export(self.private, self.private / 'public')
        self.prepare()
        path = self.private / 'result.json'
        value = json.loads(path.read_text()); value['final_target']['threads'][0]['tid'] = CANARY
        path.write_text(json.dumps(value))
        with self.assertRaises(ex.ExportError):
            self.run_export('badnumeric')


if __name__ == '__main__':
    unittest.main(verbosity=2)
