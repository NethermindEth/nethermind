# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import io
import json
from pathlib import Path
import runpy
import signal
import subprocess
import sys
import tempfile
import unittest
from contextlib import ExitStack, contextmanager
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).with_name('spin-diagnostic')))
import scheduler_collector as sc

IDENTITY = {"tid": 42, "tgid": 42, "start_ticks": 123456,
            "ppid": 7, "pgid": 42, "nspid": [42, 2]}
CONTAINER = "a" * 64
SWITCH = " 42 [003] 123.000000123: sched:sched_switch: prev_comm=.NET prev_pid=42 prev_prio=120 prev_state=S ==> next_comm=idle next_pid=0 next_prio=120\n"
WAKE = " 900 [009] 123.000100001: sched:sched_wakeup: comm=.NET pid=42 prio=120 target_cpu=003\n"
NEW = " 900 [009] 123.000200005: sched:sched_wakeup_new: comm=.NET pid=43 prio=120 target_cpu=003\n"


def fixture_proc(root, pid=42, tgid=42, start=123456, comm=".NET (thread) )"):
    root.mkdir(parents=True)
    fields = ["S", "7", str(pid)] + ["0"] * 16 + [str(start)] + ["0"] * 8
    (root / "stat").write_text(f"{pid} ({comm}) " + " ".join(fields))
    (root / "status").write_text(f"Name:\t.NET\nTgid:\t{tgid}\nNSpid:\t{pid}\t2\n")


class ParserTests(unittest.TestCase):
    def test_pid_looking_comm_does_not_invent_target_threads(self):
        for comm in ["pid=42", "pid=42 worker", "LOST pid=42"]:
            with self.subTest(comm=comm):
                switch = SWITCH.replace("prev_comm=.NET", "prev_comm=" + comm).replace(
                    "prev_pid=42", "prev_pid=500").replace("next_comm=idle", "next_comm=" + comm).replace(
                    "next_pid=0", "next_pid=501")
                wake = WAKE.replace("comm=.NET pid=42", "comm=" + comm + " pid=502")
                result = sc.parse_script([switch, wake])
                self.assertEqual(result["mentioned_tids"], [500, 501, 502])
                self.assertEqual(result["lost_records"], 0)
                self.assertEqual(result["malformed_lines"], 0)

    def test_comm_cannot_replace_a_missing_actual_field(self):
        malformed = WAKE.replace("comm=.NET pid=42", "comm=pid=42")
        self.assertEqual(sc.parse_script([malformed])["malformed_lines"], 1)

    def test_scheduler_events_preserve_ns_and_external_waker(self):
        result = sc.parse_script([SWITCH, WAKE, NEW])
        self.assertEqual(result["events"], dict.fromkeys(sc.EVENTS, 1))
        self.assertEqual(result["first_ns"], 123000000123)
        self.assertEqual(result["last_ns"], 123000200005)
        self.assertEqual(result["mentioned_tids"], [0, 42, 43])
        self.assertEqual(result["lost_records"], 0)
        self.assertEqual(result["malformed_lines"], 0)

    def test_event_contract_has_only_structured_fields(self):
        self.assertEqual(sc.parse_event(SWITCH), {
            "event": "sched_switch", "timestamp_ns": 123000000123, "cpu": 3,
            "emitter_tid": 42, "prev_pid": 42, "next_pid": 0, "prev_state": "S"})
        self.assertEqual(sc.parse_event(WAKE), {
            "event": "sched_wakeup", "timestamp_ns": 123000100001, "cpu": 9,
            "emitter_tid": 900, "pid": 42, "target_cpu": 3})
        for state in ["R", "R+", "D|P", "S+"]:
            with self.subTest(state=state):
                self.assertEqual(sc.parse_event(SWITCH.replace("prev_state=S", "prev_state=" + state))["prev_state"], state)
        for malformed in ["", "PERF_RECORD_LOST unknown", SWITCH + "extra",
                          WAKE.replace("pid=42", "pid=NaN"),
                          SWITCH.replace("prev_state=S", "prev_state=unknown"),
                          WAKE.replace("comm=.NET", "comm=" + "x" * 16)]:
            with self.subTest(malformed=malformed):
                self.assertIsNone(sc.parse_event(malformed))

    def test_loss_formats_never_become_zero_loss(self):
        for line, known, unknown in [
            ("CPU 3 lost 17 events\n", 17, 0),
            ("PERF_RECORD_LOST unknown-format\n", 0, 1),
            ("7 events lost\n", 7, 0),
        ]:
            with self.subTest(line=line):
                result = sc.parse_script([SWITCH, line])
                self.assertEqual((result["lost_records"], result["lost_events_known"],
                                  result["lost_records_unparsed"]), (1, known, unknown))

    def test_unknown_or_incomplete_format_is_not_silently_accepted(self):
        for line in ["unexpected perf output\n", WAKE.replace("target_cpu=003", ""),
                     SWITCH.replace("prev_state=S", "")]:
            with self.subTest(line=line):
                self.assertEqual(sc.parse_script([line])["malformed_lines"], 1)
        with self.assertRaises(sc.CaptureError):
            sc.parse_script(["x" * 16385])

    def test_proc_stat_parentheses_and_start_ticks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "42"
            fixture_proc(root)
            self.assertEqual(sc.proc_identity(root), IDENTITY)

    def test_container_and_thread_membership_are_checked(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture_proc(root / "42")
            fixture_proc(root / "42/task/42")
            (root / "42/cgroup").write_text("0::/system.slice/docker-" + CONTAINER + ".scope\n")
            with patch.object(sc.os, "readlink", return_value="/nethermind/nethermind"):
                snapshot = sc.target_snapshot(42, CONTAINER, root)
                self.assertEqual(snapshot["threads"], [IDENTITY])
                for wrong in ["b" * 64, "a" * 12]:
                    with self.subTest(container=wrong), self.assertRaises(sc.CaptureError):
                        sc.target_snapshot(42, wrong, root)
                (root / "42/task/42/status").write_text("Tgid:\t99\nNSpid:\t42\t2\n")
                with self.assertRaises(sc.CaptureError):
                    sc.target_snapshot(42, CONTAINER, root)


class OwnershipTests(unittest.TestCase):
    def setUp(self):
        self.enterContext(patch.object(sys,"platform","linux"))

    def test_spawn_and_pidfd_failure_never_signal_an_unproven_pid(self):
        for spawned in [False, True]:
            child = sc.OwnedProcess()
            process = Mock(pid=42)
            process.poll.return_value = None
            with self.subTest(spawned=spawned), \
                    patch.object(sc.subprocess, "Popen", return_value=process,
                                 side_effect=None if spawned else OSError("spawn failed")) as spawn, \
                    patch.object(sc.os, "pidfd_open", side_effect=OSError("pidfd failed"), create=True), \
                    patch.object(signal, "pidfd_send_signal", create=True) as send:
                with self.assertRaises(OSError):
                    child.start(["/usr/bin/perf", "record"], stdout=io.BytesIO(), stderr=io.BytesIO())
                self.assertTrue(spawn.call_args.kwargs["start_new_session"])
                self.assertNotIn("shell", spawn.call_args.kwargs)
                if spawned:
                    with self.assertRaisesRegex(sc.CaptureError, "UNKNOWN"):
                        child.stop()
                else:
                    self.assertIsNone(child.stop())
                    child.close()
                send.assert_not_called()

    def owned(self, running=True):
        child = sc.OwnedProcess()
        child.process = Mock(pid=42)
        child.process.poll.return_value = None if running else 0
        child.process.wait.return_value = 0
        child.pidfd = 123
        child.identity = IDENTITY.copy()
        return child

    def test_only_verified_pidfd_is_signalled(self):
        child = self.owned()
        with patch.object(sc, "proc_identity", return_value=IDENTITY), \
                patch.object(signal, "pidfd_send_signal", create=True) as send:
            self.assertEqual(child.stop(), 0)
            send.assert_called_once_with(123, signal.SIGINT)
            child.process.send_signal.assert_not_called()
            child.process.kill.assert_not_called()

    def test_stop_evidence_requires_a_verified_signal_and_survives_second_stop(self):
        child = self.owned()
        child.process.wait.return_value = -signal.SIGINT
        with patch.object(sc, "proc_identity", return_value=IDENTITY), \
                patch.object(signal, "pidfd_send_signal", create=True) as send:
            self.assertEqual(child.stop(), -signal.SIGINT)
            first = child.stop_evidence.copy()
            self.assertTrue(first["alive_before_stop"])
            self.assertTrue(first["sigint_sent"])
            child.process.poll.return_value = -signal.SIGINT
            self.assertEqual(child.stop(), -signal.SIGINT)
            self.assertEqual(child.stop_evidence, first)
            send.assert_called_once()

    def test_failed_signal_never_records_a_successful_stop(self):
        child = self.owned()
        with patch.object(sc, "proc_identity", return_value=IDENTITY), \
                patch.object(signal, "pidfd_send_signal", create=True, side_effect=ProcessLookupError):
            with self.assertRaises(ProcessLookupError):
                child.stop()
            self.assertTrue(child.stop_evidence["alive_before_stop"])
            self.assertFalse(child.stop_evidence["sigint_sent"])

    def test_missing_or_changed_identity_never_signals(self):
        for missing in [True, False]:
            child = self.owned()
            if missing:
                child.identity = None
            changed = IDENTITY | {"start_ticks": 999999}
            with self.subTest(missing=missing), \
                    patch.object(sc, "proc_identity", return_value=changed), \
                    patch.object(signal, "pidfd_send_signal", create=True) as send:
                with self.assertRaisesRegex(sc.CaptureError, "UNKNOWN"):
                    child.stop()
                send.assert_not_called()
                child.process.kill.assert_not_called()

    def test_stopped_child_never_signals(self):
        child = self.owned(False)
        with patch.object(signal, "pidfd_send_signal", create=True) as send, \
                patch.object(sc.os, "close") as close:
            self.assertEqual(child.stop(), 0)
            child.close()
            child.close()
            send.assert_not_called()
            close.assert_called_once_with(123)

    def test_timeout_retains_ownership_without_kill(self):
        child = self.owned()
        child.process.wait.side_effect = subprocess.TimeoutExpired("perf", 10)
        with patch.object(sc, "proc_identity", return_value=IDENTITY), \
                patch.object(signal, "pidfd_send_signal", create=True):
            with self.assertRaisesRegex(sc.CaptureError, "UNKNOWN collector termination"):
                child.stop()
            with self.assertRaises(sc.CaptureError):
                child.close()
            self.assertEqual(child.pidfd, 123)
            child.process.kill.assert_not_called()

    def test_launcher_bounds_and_parent_death_guard_without_launch(self):
        resource = Mock(RLIMIT_FSIZE=1)
        libc = Mock()
        libc.prctl.return_value = 0
        argv = ["limited_exec.py", "7", str(40 * sc.MIB), "/usr/bin/perf", "record"]
        with patch.dict(sys.modules, {"resource": resource}), \
                patch.object(sys, "argv", argv), patch("ctypes.CDLL", return_value=libc), \
                patch.object(sc.os, "getppid", return_value=7), \
                patch.object(sc.os, "umask") as umask, \
                patch.object(signal, "SIGXFSZ", 25, create=True), \
                patch.object(signal, "signal"), patch.object(sc.os, "execv") as execute:
            runpy.run_path(str(Path(sc.__file__).with_name("limited_exec.py")), run_name="__main__")
            resource.setrlimit.assert_called_once_with(1, (40 * sc.MIB, 40 * sc.MIB))
            umask.assert_called_once_with(0o077)
            libc.prctl.assert_called_once_with(1, signal.SIGINT, 0, 0, 0)
            execute.assert_called_once_with("/usr/bin/perf", ["/usr/bin/perf", "record"])


class FailureTests(unittest.TestCase):
    def test_decoder_preserves_loss_and_rejects_errors(self):
        for loss, code, diagnostics in [(False, 0, b""), (True, 0, b""),
                                         (False, 1, b""), (False, 0, b"warning")]:
            with self.subTest(loss=loss, code=code, diagnostics=diagnostics), tempfile.TemporaryDirectory() as temp:
                directory = Path(temp)
                sc.save_json(directory / "result.json", {"status": "RECORDED_REQUIRES_LINUX_VALIDATION"})
                child = Mock()
                child.process.wait.return_value = code
                child.process.poll.return_value = code

                def start(argv, *, stdout, stderr, file_limit):
                    stdout.write((SWITCH + WAKE + ("PERF_RECORD_LOST unknown\n" if loss else "")).encode())
                    stderr.write(diagnostics)
                    self.assertEqual(file_limit, 64 * sc.MIB)
                    self.assertIn("--show-lost-events", argv)

                child.start.side_effect = start
                with patch.object(sc, "OwnedProcess", return_value=child), \
                        patch.object(sys, "platform", "linux"), \
                        patch.object(sc.os, "getuid", return_value=0, create=True), \
                        patch.object(sc.stat, "S_IMODE", return_value=0o700):
                    if loss or code or diagnostics:
                        with self.assertRaises(sc.CaptureError):
                            sc.decode_capture(directory, perf=Path(sys.executable))
                    else:
                        result = sc.decode_capture(directory, perf=Path(sys.executable))
                        self.assertEqual(result["status"], "REQUIRES_CAPABILITY_AND_WINDOW_REVIEW")
                if loss:
                    result = json.loads((directory / "decode.json").read_text())
                    self.assertEqual(result["status"], "INVALID")
                    self.assertEqual(result["lost_records_unparsed"], 1)

    @contextmanager
    def simulated_linux(self, directory, *, ack=True, ack_bytes=b"ack\n", unknown=False, code=0, signal_sent=True):
        """Exercise real controller/file paths without launching or signalling a process."""
        child = Mock(identity=IDENTITY, process=Mock(pid=42))
        child.stop_evidence = None
        child.process.poll.return_value = None
        perf = Mock()
        perf.resolve.return_value = perf
        perf.is_file.return_value = True
        perf.stat.return_value.st_mode = 0o100755
        snapshot = {"clock": {}, "leader": IDENTITY, "threads": [IDENTITY]}
        thread = Mock()
        thread.is_alive.return_value = False
        original_read = Path.read_text

        def read_text(path, *args, **kwargs):
            return "test-boot" if str(path).endswith("boot_id") else original_read(path, *args, **kwargs)

        def start(*args, **kwargs):
            (directory / "sched.data").write_bytes(b"synthetic")
            if unknown:
                raise sc.CaptureError("cannot prove child ownership")

        def stop():
            if unknown:
                raise sc.CaptureError("UNKNOWN collector ownership")
            if child.stop_evidence is None:
                alive = child.process.poll() is None
                child.stop_evidence = {"alive_before_stop": alive, "sigint_sent": alive and signal_sent}
            child.process.poll.return_value = code
            return code

        child.start.side_effect, child.stop.side_effect = start, stop
        with ExitStack() as stack:
            for owner, name, value in [
                (sys, "platform", "linux"), (sc, "OwnedProcess", Mock(return_value=child)),
                (sc, "target_snapshot", Mock(return_value=snapshot)),
                (sc.os, "getuid", Mock(return_value=0)),
                (sc.os, "pidfd_open", Mock(return_value=19)), (sc.os, "close", Mock()),
                (sc.os, "pipe", Mock(side_effect=[(11, 12), (13, 14)])),
                (sc.os, "write", Mock()), (sc.os, "read", Mock(return_value=ack_bytes)),
                (sc.os, "sysconf", Mock(return_value=100)),
                (sc.stat, "S_IMODE", Mock(return_value=0o700)),
                (signal, "pidfd_send_signal", Mock()),
                (sc.select, "select", Mock(return_value=([13] if ack else [], [], []))),
                (sc.threading, "Thread", Mock(return_value=thread)),
            ]:
                stack.enter_context(patch.object(owner, name, value, create=True))
            stack.enter_context(patch.object(Path, "read_text", read_text))
            collector = sc.SchedulerCollector(directory, 42, CONTAINER)
            collector.perf = perf
            yield collector, child

    def test_full_lifecycle_and_missing_ack(self):
        for ack, ack_bytes, unknown, valid in [
            (True, b"ack\n", False, True),
            (True, b"ack\n\0", False, True),
            (False, b"ack\n", False, False),
            (True, b"", False, False),
            (True, b"ack", False, False),
            (True, b"ack\0", False, False),
            (True, b"ack\n\0\0", False, False),
            (True, b"ack\nextra", False, False),
            (True, b"ack\nack\n", False, False),
            (True, b"\0" * 64, False, False),
            (True, b"ack\n", True, False),
        ]:
            with self.subTest(ack=ack, ack_bytes=ack_bytes, unknown=unknown), tempfile.TemporaryDirectory() as temp:
                path = Path(temp) / "capture"
                with self.simulated_linux(path, ack=ack, ack_bytes=ack_bytes, unknown=unknown) as (collector, child):
                    if valid:
                        with collector:
                            self.assertEqual(collector.metadata["status"], "RECORDING")
                    else:
                        with self.assertRaises(sc.CaptureError):
                            collector.__enter__()
                    result = json.loads((path / "result.json").read_text())
                    expected = "UNKNOWN_RETAIN_OWNERSHIP" if unknown else (
                        "RECORDED_REQUIRES_LINUX_VALIDATION" if valid else "FAILED")
                    self.assertEqual(result["status"], expected)
                    self.assertEqual((path / "started.json").exists(), valid)
                    self.assertEqual("enable_ack" in result, valid)
                    self.assertEqual(collector.monitor is not None, valid)
                    if not unknown:
                        self.assertEqual(result["enable_reply"], {
                            "ready": ack, "length": len(ack_bytes) if ack else 0,
                            "hex": ack_bytes.hex() if ack else ""})
                        sc.select.select.assert_called_once_with([13], [], [], 5)
                        sc.os.write.assert_called_once_with(12, b"enable\n")
                        if ack:
                            sc.os.read.assert_called_once_with(13, 64)
                        else:
                            sc.os.read.assert_not_called()
                    self.assertFalse(result["thread_map_complete"])
                    child.stop.assert_called_once()
                    child.process.kill.assert_not_called()

    def test_acknowledged_child_must_still_be_alive_before_replay(self):
        for ack_bytes, code in [(b"ack\n", 0), (b"ack\n\0", -signal.SIGINT)]:
            with self.subTest(ack_bytes=ack_bytes, code=code), tempfile.TemporaryDirectory() as temp:
                path = Path(temp) / "capture"
                with self.simulated_linux(path, ack_bytes=ack_bytes, code=code) as (collector, child):
                    child.process.poll.return_value = code
                    with self.assertRaisesRegex(sc.CaptureError, "perf exited before replay"):
                        collector.__enter__()
                    result = json.loads((path / "result.json").read_text())
                    self.assertEqual(result["status"], "FAILED")
                    self.assertEqual(result["enable_reply"], {
                        "ready": True, "length": len(ack_bytes), "hex": ack_bytes.hex()})
                    self.assertFalse((path / "started.json").exists())
                    self.assertNotIn("enable_ack", result)
                    self.assertIsNone(collector.monitor)
                    self.assertFalse(result["owned_stop"]["alive_before_stop"])
                    self.assertFalse(result["owned_stop"]["sigint_sent"])
                    child.stop.assert_called_once()

    def test_completion_requires_controlled_live_stop_and_successful_replay(self):
        for code, early, signal_sent, replay_error, watchdog, valid in [
            (0, False, True, False, False, True),
            (-signal.SIGINT, False, True, False, False, True),
            (0, True, True, False, False, False),
            (-signal.SIGINT, True, True, False, False, False),
            (-signal.SIGINT, False, False, False, False, False),
            (-signal.SIGTERM, False, True, False, False, False),
            (-signal.SIGINT, False, True, True, False, False),
            (-signal.SIGINT, False, True, False, True, False),
        ]:
            with self.subTest(code=code, early=early, signal_sent=signal_sent,
                              replay_error=replay_error, watchdog=watchdog), tempfile.TemporaryDirectory() as temp:
                path = Path(temp) / "capture"
                with self.simulated_linux(path, code=code, signal_sent=signal_sent) as (collector, child):
                    collector.__enter__()
                    if early:
                        child.process.poll.return_value = code
                    if watchdog:
                        collector.error = "capture time limit reached before replay end"
                    if valid:
                        collector.__exit__(None, None, None)
                    else:
                        with self.assertRaises(sc.CaptureError):
                            collector.__exit__(RuntimeError if replay_error else None, None, None)
                    result = json.loads((path / "result.json").read_text())
                    self.assertEqual(result["status"], "RECORDED_REQUIRES_LINUX_VALIDATION" if valid else "FAILED")
                    self.assertEqual(result["record_returncode"], code)
                    self.assertEqual(result["owned_stop"]["alive_before_stop"], not early)
                    self.assertEqual(result["owned_stop"]["sigint_sent"], not early and signal_sent)
                    self.assertEqual(result["replay_completed"], not replay_error)

    def test_deadline_at_replay_end_is_checked_without_monitor_tick(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "capture"
            with self.simulated_linux(path, code=-signal.SIGINT) as (collector, child):
                collector.__enter__()
                collector.deadline = 0
                with self.assertRaisesRegex(sc.CaptureError, "time limit"):
                    collector.__exit__(None, None, None)
                self.assertEqual(json.loads((path / "result.json").read_text())["status"], "FAILED")

    def test_platform_rejected_before_creating_directory(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(sys, "platform", "win32"):
            path = Path(directory) / "capture"
            with self.assertRaises(sc.CaptureError):
                sc.SchedulerCollector(path, 42, CONTAINER).__enter__()
            self.assertFalse(path.exists())

    def test_watchdog_fails_and_stops_only_its_child(self):
        collector = sc.SchedulerCollector(Path("unused"), 42, CONTAINER, seconds=1)
        collector.deadline = 0
        collector.done = Mock()
        collector.done.wait.return_value = False
        with patch.object(collector, "_snapshot"), patch.object(collector, "_stop") as stop:
            collector._monitor()
            self.assertIn("time limit", collector.error)
            stop.assert_called_once()

    def test_unknown_stop_persists_no_success_marker(self):
        with tempfile.TemporaryDirectory() as directory:
            collector = sc.SchedulerCollector(Path(directory), 42, CONTAINER)
            collector.child.process = Mock(pid=42)
            collector.child.process.poll.return_value = None
            collector.metadata["status"] = "FAILED"
            collector._finish()
            result = json.loads((Path(directory) / "result.json").read_text())
            self.assertEqual(result["status"], "UNKNOWN_RETAIN_OWNERSHIP")
            self.assertEqual(result["unconfirmed_child_pid"], 42)

    def test_metadata_is_exclusive_and_bounded(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "meta.json"
            with self.assertRaises(sc.CaptureError):
                sc.save_json(path, "x" * (4 * sc.MIB))
            self.assertFalse(path.exists())
            sc.save_json(path, {"valid": True})
            with self.assertRaises(FileExistsError):
                sc.save_json(path, {})

    def test_incomplete_record_is_not_decoded(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            sc.save_json(path / "result.json", {"status": "UNKNOWN_RETAIN_OWNERSHIP"})
            with patch.object(sc.subprocess, "Popen") as spawn:
                with self.assertRaises(sc.CaptureError):
                    sc.decode_capture(path)
                spawn.assert_not_called()


if __name__ == "__main__":
    unittest.main(verbosity=2)
