# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Experimental EXPB sidecar. No CLI, remote operations or workload management."""
from __future__ import annotations

import json
from contextlib import ExitStack
import os
from pathlib import Path
import re
import select
import shutil
import signal
import stat
import subprocess
import sys
import threading
import time

if __package__:
    from . import pidfd_compat
else:
    import pidfd_compat

MIB = 1024 * 1024
RECORD_LIMIT = 512 * MIB
RECORD_FILE_LIMIT = 520 * MIB
SCRIPT_LIMIT = 2048 * MIB
PROJECTION_LIMIT = 512 * MIB
DECODER_TIMEOUT = 300
STORAGE_RESERVE = 1024 * MIB
# Each child can fill both its data and diagnostic file; allow two archive copies.
CAPTURE_FREE_BYTES = 3 * (2 * RECORD_FILE_LIMIT + 2 * SCRIPT_LIMIT) + PROJECTION_LIMIT + STORAGE_RESERVE
DECODE_FREE_BYTES = 2 * SCRIPT_LIMIT + PROJECTION_LIMIT + STORAGE_RESERVE
EVENTS = ("sched_switch", "sched_wakeup", "sched_wakeup_new")
LINE = re.compile(
    r"^\s*(?P<tid>\d+)\s+\[(?P<cpu>\d+)\]\s+"
    r"(?P<seconds>\d+)\.(?P<fraction>\d{1,9}):\s+"
    r"sched:(?P<event>\w+):\s+(?P<fields>.+)$"
)
# Linux TASK_COMM_LEN includes the terminator; names can contain spaces and '='.
SWITCH_FIELDS = re.compile(
    r"prev_comm=[^\r\n]{0,15} prev_pid=(?P<prev_pid>\d+) prev_prio=\d+ "
    r"prev_state=(?P<prev_state>(?:R|[SDTtXZPI](?:\|[SDTtXZPI])*)\+?) ==> "
    r"next_comm=[^\r\n]{0,15} next_pid=(?P<next_pid>\d+) next_prio=\d+"
)
WAKE_FIELDS = re.compile(
    r"comm=[^\r\n]{0,15} pid=(?P<pid>\d+) prio=\d+ target_cpu=(?P<target_cpu>\d+)"
)


class CaptureError(RuntimeError):
    pass


def check_free_space(directory, required):
    free = shutil.disk_usage(directory).free
    if free < required:
        raise CaptureError("insufficient free space for bounded diagnostic outputs")
    return {"free_bytes": free, "required_bytes": required}


def clock_origin():
    before = time.monotonic_ns()
    realtime = time.time_ns()
    after = time.monotonic_ns()
    return {"monotonic_before_ns": before, "realtime_ns": realtime,
            "monotonic_after_ns": after}


def proc_identity(path: Path):
    data = (path / "stat").read_text()
    head, tail = data.split(" (", 1)
    fields = tail.rsplit(") ", 1)[1].split()
    status = dict(line.split(":", 1) for line in (path / "status").read_text().splitlines())
    result = {"tid": int(head), "start_ticks": int(fields[19]),
              "ppid": int(fields[1]), "pgid": int(fields[2]),
              "tgid": int(status["Tgid"]),
              "nspid": [int(x) for x in status["NSpid"].split()]}
    if result["start_ticks"] <= 0 or not result["nspid"]:
        raise CaptureError("invalid proc identity")
    repeated = (path / "stat").read_text().rsplit(") ", 1)[1].split()
    if int(repeated[19]) != result["start_ticks"] or result["nspid"][0] != result["tid"]:
        raise CaptureError("proc identity changed during read")
    return result


def target_snapshot(pid: int, container_id: str, proc=Path("/proc")):
    if not re.fullmatch(r"[0-9a-f]{64}", container_id):
        raise CaptureError("full Docker container id required")
    path = proc / str(pid)
    leader = proc_identity(path)
    cgroup = (path / "cgroup").read_text()
    if leader["tgid"] != pid or not re.search(r"[/:-]" + container_id + r"(?:\.scope|/|$)", cgroup, re.M):
        raise CaptureError("target does not belong to expected container")
    executable = Path(os.readlink(path / "exe")).name.lower()
    if executable not in ("nethermind", "dotnet"):
        raise CaptureError("target is not the client executable")
    if executable == "dotnet" and not any(
        Path(part).name.lower() == "nethermind.dll"
        for part in (path / "cmdline").read_text().split("\0")
    ):
        raise CaptureError("dotnet target is not Nethermind")
    threads = []
    tasks = list((path / "task").iterdir())
    if len(tasks) > 4096:
        raise CaptureError("thread count limit exceeded")
    for task in sorted(tasks, key=lambda p: int(p.name)):
        try:
            item = proc_identity(task)
        except FileNotFoundError:
            continue
        if item["tgid"] != pid:
            raise CaptureError("unexpected thread owner")
        threads.append(item)
    if not threads or proc_identity(path)["start_ticks"] != leader["start_ticks"]:
        raise CaptureError("target changed during snapshot")
    return {"clock": clock_origin(), "leader": leader, "threads": threads}


def save_json(path: Path, value):
    data = (json.dumps(value, indent=2) + "\n").encode()
    if len(data) > 4 * MIB:
        raise CaptureError("metadata limit exceeded")
    with path.open("xb") as out:
        out.write(data)


def parse_event(line):
    """Return only structured scheduler fields, or None for an unknown format."""
    if len(line) > 16384:
        raise CaptureError("oversized script line")
    match = LINE.fullmatch(line.rstrip("\r\n"))
    if not match or match["event"] not in EVENTS:
        return None
    pattern = SWITCH_FIELDS if match["event"] == "sched_switch" else WAKE_FIELDS
    fields = pattern.fullmatch(match["fields"])
    if fields is None:
        return None
    event = {"event": match["event"], "cpu": int(match["cpu"]),
             "emitter_tid": int(match["tid"]),
             "timestamp_ns": int(match["seconds"]) * 1_000_000_000
             + int(match["fraction"].ljust(9, "0"))}
    event.update({name: value if name == "prev_state" else int(value)
                  for name, value in fields.groupdict().items()})
    return event


def parse_script(lines):
    """Validate explicit perf-script format; never turn unknown loss into zero."""
    counts = {name: 0 for name in EVENTS}
    first = last = None
    lost_records = lost_known = lost_unknown = malformed = 0
    tids = set()
    for line in lines:
        event = parse_event(line)
        if event is None and "LOST" in line.upper():
            lost_records += 1
            amount = re.search(r"(?:lost\s+(\d+)|(\d+)\s+events?\s+lost)", line, re.I)
            if amount:
                lost_known += int(next(g for g in amount.groups() if g is not None))
            else:
                lost_unknown += 1
            continue
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if event is None:
            malformed += 1
            continue
        timestamp = event["timestamp_ns"]
        first = timestamp if first is None else min(first, timestamp)
        last = timestamp if last is None else max(last, timestamp)
        counts[event["event"]] += 1
        # The emitting TID can be an external waker. Never filter by emitter alone.
        tids.update((event["prev_pid"], event["next_pid"]) if event["event"] == "sched_switch" else (event["pid"],))
        if len(tids) > 65536:
            raise CaptureError("distinct TID limit exceeded")
    return {"events": counts, "first_ns": first, "last_ns": last,
            "lost_records": lost_records, "lost_events_known": lost_known,
            "lost_records_unparsed": lost_unknown, "malformed_lines": malformed,
            "mentioned_tids": sorted(tids)}


class OwnedProcess:
    """A pidfd pins our direct child across PID reuse; no process-group signals."""

    def __init__(self):
        self.process = self.pidfd = self.identity = None
        self.stop_evidence = None

    def start(self, argv, *, stdout, stderr, pass_fds=(), file_limit=RECORD_FILE_LIMIT):
        launcher = str(Path(__file__).with_name("limited_exec.py"))
        self.process = subprocess.Popen(
            [sys.executable, "-I", launcher, str(os.getpid()), str(file_limit), *argv],
            stdout=stdout, stderr=stderr, pass_fds=pass_fds, start_new_session=True,
        )
        self.pidfd = pidfd_compat.pidfd_open(self.process.pid)
        self.identity = proc_identity(Path("/proc") / str(self.process.pid))
        if self.identity["ppid"] != os.getpid() or self.identity["pgid"] != self.process.pid:
            raise CaptureError("UNKNOWN collector ownership; no signal sent")

    def stop(self):
        if self.process is None:
            return None
        alive = self.process.poll() is None
        if self.stop_evidence is None:
            self.stop_evidence = {"alive_before_stop": alive, "sigint_sent": False}
        if alive and not self.stop_evidence["sigint_sent"]:
            if self.pidfd is None or self.identity is None:
                raise CaptureError("UNKNOWN collector ownership; no signal sent")
            current = proc_identity(Path("/proc") / str(self.process.pid))
            if current != self.identity:
                raise CaptureError("UNKNOWN collector ownership; no signal sent")
            pidfd_compat.pidfd_send_signal(self.pidfd, signal.SIGINT)
            self.stop_evidence["sigint_sent"] = True
            self.stop_evidence["signal_sent"] = clock_origin()
        try:
            return self.process.wait(timeout=10)
        except subprocess.TimeoutExpired as error:
            # Retain process identity and files; no escalation to a broad kill.
            raise CaptureError("UNKNOWN collector termination") from error

    def close(self):
        if self.process is not None and self.process.poll() is None:
            raise CaptureError("cannot release live collector ownership")
        if self.pidfd is not None:
            os.close(self.pidfd)
            self.pidfd = None


class SchedulerCollector:
    def __init__(self, directory: Path, host_pid: int, container_id: str,
                 *, perf=Path("/usr/bin/perf"), seconds=120):
        if not 1 <= seconds <= 120:
            raise ValueError("capture bound must be 1..120 seconds")
        self.directory, self.pid, self.container_id = Path(directory).resolve(), host_pid, container_id
        self.perf, self.seconds = Path(perf), seconds
        self.child = OwnedProcess()
        self.record_log = self.ctl_write = self.ack_read = self.monitor = None
        self.done = threading.Event()
        self.mutex = threading.Lock()
        self.error = None
        self.snapshots = []
        self.mapping_bytes = 0
        self.metadata = {"status": "PREPARING", "clock_id": "CLOCK_MONOTONIC",
                         "container_id": container_id, "window_limit_seconds": seconds,
                         "thread_map_complete": False}

    def _snapshot(self):
        snapshot = target_snapshot(self.pid, self.container_id)
        if self.snapshots and snapshot["leader"] != self.snapshots[0]["leader"]:
            raise CaptureError("target process identity changed")
        # Store observations only when membership changes, plus explicit boundaries.
        if not self.snapshots or snapshot["threads"] != self.snapshots[-1]["threads"]:
            self.mapping_bytes += len(json.dumps(snapshot))
            if self.mapping_bytes > 2 * MIB:
                raise CaptureError("thread mapping byte limit exceeded")
            self.snapshots.append(snapshot)
        return snapshot

    def __enter__(self):
        try:
            pidfd_compat.preflight()
        except OSError as error:
            raise CaptureError("Linux pidfd capability failed: " + str(error)) from error
        self.perf = self.perf.resolve(strict=True)
        if not self.perf.is_file() or self.perf.stat().st_mode & 0o022:
            raise CaptureError("perf executable is missing or broadly writable")
        self.directory.mkdir(mode=0o700, parents=False, exist_ok=False)
        info = self.directory.lstat()
        if info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
            raise CaptureError("private directory validation failed")
        try:
            return self._start()
        except BaseException as error:
            self.metadata["status"] = "FAILED"
            self.metadata["error"] = str(error)
            try:
                self._stop()
            except Exception as stop_error:
                self.metadata["stop_error"] = str(stop_error)
            self._finish()
            raise

    def _start(self):
        self.metadata["storage_preflight"] = check_free_space(self.directory, CAPTURE_FREE_BYTES)
        self._snapshot()
        self.metadata["boot_id"] = Path("/proc/sys/kernel/random/boot_id").read_text().strip()
        self.metadata["clock_ticks_per_second"] = os.sysconf("SC_CLK_TCK")
        with ExitStack() as descriptors:
            ctl_read, self.ctl_write = os.pipe()
            descriptors.callback(os.close, ctl_read)
            self.ack_read, ack_write = os.pipe()
            descriptors.callback(os.close, ack_write)
            argv = [str(self.perf), "record", "--all-cpus", "--clockid", "mono",
                    "--no-buildid", "--no-buildid-cache", "--synth=no",
                    "--mmap-pages=256", f"--max-size={RECORD_LIMIT // MIB}M", "--delay=-1",
                    f"--control=fd:{ctl_read},{ack_write}",
                    "--output", str(self.directory / "sched.data")]
            for event in EVENTS:
                argv.extend(["--event", "sched:" + event])
            self.metadata["command"] = argv
            self.record_log = (self.directory / "perf-record.log").open("xb")
            self.child.start(argv, stdout=self.record_log, stderr=self.record_log,
                             pass_fds=(ctl_read, ack_write))
            self.metadata["collector"] = self.child.identity
        self.metadata["enable_before"] = clock_origin()
        os.write(self.ctl_write, b"enable\n")
        ready, _, _ = select.select([self.ack_read], [], [], 5)
        reply = os.read(self.ack_read, 64) if ready else b""
        self.metadata["enable_reply"] = {"ready": bool(ready), "length": len(reply), "hex": reply.hex()}
        if not ready or reply not in (b"ack\n", b"ack\n\0"):
            raise CaptureError("perf enable acknowledgement missing")
        if self.child.process.poll() is not None:
            raise CaptureError("perf exited before replay")
        self.metadata["enable_ack"] = clock_origin()
        self.metadata["status"] = "RECORDING"
        save_json(self.directory / "started.json", self.metadata | {"initial_target": self.snapshots[0]})
        self.deadline = time.monotonic() + self.seconds
        self.monitor = threading.Thread(target=self._monitor, name="sched-capture-bound", daemon=True)
        self.monitor.start()
        return self

    def _monitor(self):
        try:
            while not self.done.wait(0.5):
                self._snapshot()
                if time.monotonic() >= self.deadline:
                    raise CaptureError("capture time limit reached before replay end")
                if self.child.process.poll() is not None:
                    raise CaptureError("collector exited during replay (possibly size cap)")
                if len(self.snapshots) > 2000:
                    raise CaptureError("thread mapping limit reached")
        except Exception as error:
            self.error = str(error)
            try:
                self._stop()
            except Exception as stop_error:
                self.error += "; " + str(stop_error)

    def _stop(self):
        with self.mutex:
            self.done.set()
            self.metadata.setdefault("stop_requested", clock_origin())
            if self.child.process is not None:
                try:
                    self.metadata["record_returncode"] = self.child.stop()
                finally:
                    self.metadata["owned_stop"] = self.child.stop_evidence
            self.metadata["stopped"] = clock_origin()

    def __exit__(self, exc_type, exc, traceback):
        failure = None
        self.metadata["replay_completed"] = exc_type is None
        try:
            if time.monotonic() >= self.deadline:
                self.error = self.error or "capture time limit reached before replay end"
            self._stop()
            self.monitor.join(timeout=12)
            if self.monitor.is_alive():
                raise CaptureError("monitor did not finish")
            self.metadata["final_target"] = self._snapshot()
            self.metadata["snapshots"] = self.snapshots
            stop = self.metadata.get("owned_stop") or {}
            controlled = stop.get("alive_before_stop") is True and stop.get("sigint_sent") is True
            if (self.error or exc_type or not controlled
                    or self.metadata["record_returncode"] not in (0, -signal.SIGINT)):
                raise CaptureError(self.error or "replay or perf record failed")
            if not 0 < (self.directory / "sched.data").stat().st_size < RECORD_LIMIT:
                raise CaptureError("record empty or at size limit")
            self.metadata["status"] = "RECORDED_REQUIRES_LINUX_VALIDATION"
        except Exception as error:
            failure = error
            self.metadata["status"] = "FAILED"
            self.metadata["error"] = str(error)
        finally:
            self._finish()
        if failure:
            raise failure

    def _finish(self):
        self.metadata["snapshots"] = self.snapshots
        if self.child.process is not None and self.child.process.poll() is None:
            self.metadata["status"] = "UNKNOWN_RETAIN_OWNERSHIP"
            self.metadata["unconfirmed_child_pid"] = self.child.process.pid
            self.metadata["unconfirmed_child_identity"] = self.child.identity
        else:
            self.child.close()
            for descriptor in (self.ctl_write, self.ack_read):
                if descriptor is not None:
                    os.close(descriptor)
        if self.record_log is not None:
            self.record_log.close()
        save_json(self.directory / "result.json", self.metadata)


def decode_capture(directory: Path, *, perf=Path("/usr/bin/perf")):
    """Offline on Linux after successful record; bounded decoder and raw timestamped text."""
    directory = Path(directory)
    if sys.platform != "linux":
        raise CaptureError("Linux decoder required")
    info = directory.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
        raise CaptureError("decoder requires the original private directory")
    result = json.loads((directory / "result.json").read_text())
    if result["status"] != "RECORDED_REQUIRES_LINUX_VALIDATION":
        raise CaptureError("refusing incomplete/unknown capture")
    if not 0 < (directory / "sched.data").stat().st_size < RECORD_LIMIT:
        raise CaptureError("record empty or at size limit")
    storage = check_free_space(directory, DECODE_FREE_BYTES)
    argv = [str(perf.resolve(strict=True)), "script", "--ns", "--show-lost-events",
            "--input", str(directory / "sched.data"), "-F", "tid,cpu,time,event,trace"]
    with (directory / "sched.script.txt").open("xb") as output, \
            (directory / "perf-script.log").open("xb") as errors:
        child = OwnedProcess()
        decoder = {"status": "STARTING", "command": argv, "storage_preflight": storage}
        failure = None
        try:
            child.start(argv, stdout=output, stderr=errors, file_limit=SCRIPT_LIMIT)
            code = child.process.wait(timeout=DECODER_TIMEOUT)
            decoder.update(status="EXITED", returncode=code)
        except BaseException as error:
            failure = error
            decoder.update(status="FAILED", error=str(error))
            try:
                child.stop()
            except Exception as stop_error:
                decoder["stop_error"] = str(stop_error)
        finally:
            if child.process is not None and child.process.poll() is None:
                decoder.update(status="UNKNOWN_RETAIN_OWNERSHIP", pid=child.process.pid,
                               identity=child.identity)
            else:
                child.close()
            save_json(directory / "decoder-status.json", decoder)
        if failure:
            raise CaptureError("decoder failed; inspect private decoder-status.json") from failure
    if code != 0 or (directory / "perf-script.log").stat().st_size:
        raise CaptureError("decoder failed or emitted diagnostics; inspect private log")
    if not 0 < (directory / "sched.script.txt").stat().st_size < SCRIPT_LIMIT:
        raise CaptureError("decoder output empty or at size limit")
    with (directory / "sched.script.txt").open() as stream:
        summary = parse_script(stream)
    summary["status"] = "REQUIRES_CAPABILITY_AND_WINDOW_REVIEW"
    if summary["lost_records"] or summary["malformed_lines"] or not summary["events"]["sched_switch"]:
        summary["status"] = "INVALID"
    save_json(directory / "decode.json", summary)
    if summary["status"] == "INVALID":
        raise CaptureError("lost, malformed or empty scheduler trace")
    return summary
