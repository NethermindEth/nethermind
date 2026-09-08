#!/usr/bin/env python3
"""Run the pinned EXPB CLI with an Account-index preparation hook.

The hook is deliberately installed in the Python environment that owns EXPB's
``expb = \"expb:app\"`` console entry point.  EXPB therefore retains its normal
scenario, container, and cleanup lifecycle; the only inserted operation is the
Account SST rewrite after the overlay mount and before the client container is
started.
"""

from __future__ import annotations

import hashlib
import importlib
import inspect
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Sequence


REPO_ROOT = Path(__file__).resolve().parents[2]
RPC_BENCH_SCRIPTS = REPO_ROOT / "scripts" / "rpc-bench"
if str(RPC_BENCH_SCRIPTS) not in sys.path:
    sys.path.insert(0, str(RPC_BENCH_SCRIPTS))


IMAGE_TAG = "nethermindeth/nethermind:rocksdb-auto-base-d66cfd50e0"
IMAGE_DIGEST = "sha256:381e9176ba81ec40fa3e55aa77157b8d1364f74012e55041d939860e3279a745"
IMAGE = f"{IMAGE_TAG}@{IMAGE_DIGEST}"
EXPB_REVISION = "9609a1b66aba5b67c6accc1930cb3e50748ca72f"
MARKER = ".account-index-prepare-owned"
ACCOUNT_OPTIONS = "block_based_table_factory.index_block_search_type"
UNIFORM_OPTIONS = "block_based_table_factory.uniform_cv_threshold"


@dataclass(frozen=True)
class Arm:
    label: str
    mode: str
    threshold: float
    search_type: str


ARMS: tuple[Arm, ...] = (
    Arm("master", "binary", -1.0, "kBinary"),
    Arm("forced-interpolation", "interpolation", -1.0, "kInterpolation"),
    Arm("auto-cv-0.1", "auto", 0.1, "kAuto"),
    Arm("auto-cv-0.2", "auto", 0.2, "kAuto"),
    Arm("auto-cv-0.5", "auto", 0.5, "kAuto"),
)
ARM_BY_LABEL = {arm.label: arm for arm in ARMS}


class HarnessError(RuntimeError):
    """Raised when the pinned preparation contract cannot be established."""


def arm_for_label(label: str) -> Arm:
    try:
        return ARM_BY_LABEL[label]
    except KeyError as error:
        raise HarnessError(f"unknown Account CV arm {label!r}") from error


def arm_for_snapshot_name(name: str) -> Arm:
    for arm in ARMS:
        if name == f"expb-executor-{safe_executor_name(arm.label)}":
            return arm
    raise HarnessError(f"unknown Account CV snapshot name {name!r}")


def safe_executor_name(label: str) -> str:
    """Return EXPB's deterministic executor suffix for a scenario label."""

    return re.sub(r"[^a-zA-Z0-9_-]", "-", label)


def expected_client_container(label: str) -> str:
    return f"expb-executor-{safe_executor_name(label)}-nethermind"


def account_extra_flag(arm: Arm) -> str:
    return (
        f"--Db.FlatAccountDbAdditionalRocksDbOptions="
        f"{ACCOUNT_OPTIONS}={arm.search_type};{UNIFORM_OPTIONS}={arm.threshold:g};"
    )


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _inside(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
    except ValueError:
        return False
    return path != root


def _is_mountpoint(path: Path) -> bool:
    if not path.exists():
        return False
    result = subprocess.run(
        ["mountpoint", "-q", str(path)],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    return result.returncode == 0


def _unmount(path: Path) -> None:
    if not _is_mountpoint(path):
        return
    result = subprocess.run(
        ["umount", "--", str(path)],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )
    if result.returncode != 0 or _is_mountpoint(path):
        detail = result.stdout.strip()
        raise HarnessError(f"could not unmount owned overlay {path}: {detail}")


def _remove_unmounted(path: Path) -> None:
    if _is_mountpoint(path):
        raise HarnessError(f"refusing to remove mounted overlay path {path}")
    if path.exists():
        shutil.rmtree(path)


def _write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


class SnapshotPreparationHook:
    """Prepare and validate one isolated EXPB overlay for one fixed arm."""

    def __init__(
        self,
        scratch_root: Path,
        results_root: Path,
        helper_path: Path,
        image: str = IMAGE,
        expected_arm: str | None = None,
        helper_cpuset: str | None = None,
        helper_memory: str | None = None,
        mount_checker: Callable[[Path], bool] = _is_mountpoint,
        unmount: Callable[[Path], None] = _unmount,
    ) -> None:
        self.scratch_root = scratch_root.resolve()
        self.results_root = results_root.resolve()
        self.helper_path = helper_path.resolve()
        self.image = image
        self.expected_arm = expected_arm
        self.helper_cpuset = helper_cpuset
        self.helper_memory = helper_memory
        self.mount_checker = mount_checker
        self.unmount = unmount
        self.canonical_path = self.results_root / "canonical-fingerprint.json"
        self._active_container: str | None = None
        self._active_process: subprocess.Popen[str] | None = None
        self.scratch_root.mkdir(parents=True, exist_ok=True)
        self.results_root.mkdir(parents=True, exist_ok=True)
        if not self.helper_path.is_file() or not os.access(self.helper_path, os.X_OK):
            raise HarnessError(f"Account helper is not executable: {self.helper_path}")
        if not _inside(self.results_root, self.scratch_root) and self.results_root != self.scratch_root:
            raise HarnessError("result root must be inside the dedicated task scratch root")
        if self.helper_path == self.scratch_root or _inside(self.helper_path, self.scratch_root):
            raise HarnessError("helper must be outside the task scratch root")

    @staticmethod
    def service_paths(service: Any) -> tuple[Path, Path, Path]:
        try:
            work = Path(service.overlay_work_dir).resolve()
            upper = Path(service.overlay_upper_dir).resolve()
            merged = Path(service.overlay_merged_dir).resolve()
        except (AttributeError, TypeError) as error:
            raise HarnessError("pinned OverlaySnapshotService path contract changed") from error
        return work, upper, merged

    def _validate_owned_paths(self, paths: tuple[Path, Path, Path]) -> None:
        for path in paths:
            if not _inside(path, self.scratch_root):
                raise HarnessError(f"overlay path is outside dedicated scratch: {path}")
        for index, path in enumerate(paths):
            for other in paths[index + 1 :]:
                if path == other or _inside(path, other) or _inside(other, path):
                    raise HarnessError(f"overlay paths overlap: {path} and {other}")
        for path in paths:
            if path == self.results_root or _inside(path, self.results_root) or _inside(self.results_root, path):
                raise HarnessError(f"overlay path overlaps result root: {path}")

    def _ensure_fresh(self, paths: tuple[Path, Path, Path]) -> None:
        self._validate_owned_paths(paths)
        for path in paths:
            if self.mount_checker(path):
                raise HarnessError(f"stale overlay mount exists at {path}")
            if path.exists() and any(path.iterdir()):
                raise HarnessError(f"stale overlay directory is not empty: {path}")

    def _result_dir(self, arm: Arm) -> Path:
        target = (self.results_root / arm.label).resolve()
        if not _inside(target, self.results_root):
            raise HarnessError("arm result directory escaped the task scratch root")
        target.mkdir(parents=True, exist_ok=True)
        return target

    def _stop_helper(self) -> None:
        process = self._active_process
        if process is not None and process.poll() is None:
            try:
                process.send_signal(signal.SIGTERM)
                process.wait(timeout=10)
            except (subprocess.TimeoutExpired, OSError):
                process.kill()
                process.wait(timeout=10)
        self._active_process = None
        if self._active_container is not None:
            subprocess.run(
                ["docker", "rm", "-f", self._active_container],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            self._active_container = None

    def _run_helper(self, arm: Arm, merged: Path, result_dir: Path) -> None:
        container = f"account-index-prepare-{safe_executor_name(arm.label)}-{os.getpid()}"
        helper_log = result_dir / "helper.log"
        command = [
            "docker",
            "run",
            "--name",
            container,
            "--label",
            "expb-account-cv",
            "--network",
            "none",
            "-v",
            f"{merged}:/work:rw",
            "-v",
            f"{self.helper_path.parent}:/opt/account-index-prepare:ro",
            "-v",
            f"{result_dir}:/result:rw",
        ]
        if self.helper_cpuset:
            command.extend(["--cpuset-cpus", self.helper_cpuset])
        if self.helper_memory:
            command.extend(["--memory", self.helper_memory])
        command.extend(
            [
                "--entrypoint",
                "/opt/account-index-prepare/AccountIndexPrepare",
                self.image,
                "--db-path",
                "/work/mainnet/flat",
                "--scratch-root",
                "/work",
                "--mode",
                arm.mode,
                "--threshold",
                f"{arm.threshold:g}",
                "--output",
                "/result/helper.json",
            ]
        )
        self._active_container = container
        with helper_log.open("w", encoding="utf-8") as stream:
            try:
                self._active_process = subprocess.Popen(
                    command,
                    stdout=stream,
                    stderr=subprocess.STDOUT,
                    text=True,
                    start_new_session=True,
                )
                while True:
                    try:
                        status = self._active_process.wait(timeout=1)
                        break
                    except subprocess.TimeoutExpired:
                        continue
            except BaseException:
                self._stop_helper()
                raise
            finally:
                self._active_process = None
        if status != 0:
            raise HarnessError(f"Account helper failed for {arm.label} with exit code {status}")
        subprocess.run(
            ["docker", "rm", "-f", container],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        self._active_container = None

    def _validate_helper(self, arm: Arm, result_dir: Path) -> dict[str, Any]:
        raw = result_dir / "helper.json"
        sanitized = result_dir / "account-index-prepare.json"
        if not raw.is_file():
            raise HarnessError(f"Account helper produced no report for {arm.label}")
        helper_sha = _sha256(self.helper_path)
        try:
            module = importlib.import_module("account_index_sweep")
            module.validate_result_file(
                str(raw),
                str(sanitized),
                arm.mode,
                arm.threshold,
                helper_sha,
            )
        except Exception as error:
            raise HarnessError(f"Account helper report failed validation for {arm.label}: {error}") from error
        try:
            value = json.loads(sanitized.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError) as error:
            raise HarnessError(f"sanitized Account report is unreadable: {error}") from error
        if not isinstance(value, dict):
            raise HarnessError("sanitized Account report is not an object")
        return value

    def _record_fingerprint(self, arm: Arm, report: dict[str, Any], result_dir: Path) -> None:
        raw = json.loads((result_dir / "helper.json").read_text(encoding="utf-8"))
        content = raw["Content"]
        before = content["Before"]["Sha256"]
        after = content["After"]["Sha256"]
        if before != after or before != report["account_content_sha256"]:
            raise HarnessError(f"Account content fingerprint changed for {arm.label}")
        fingerprint = {
            "schema_version": 1,
            "account_entry_count": report["account_entry_count"],
            "account_content_sha256_before": before,
            "account_content_sha256_after": after,
            "helper_sha256": report["helper_sha256"],
        }
        if self.canonical_path.exists():
            current = json.loads(self.canonical_path.read_text(encoding="utf-8"))
            for key in fingerprint:
                if current.get(key) != fingerprint[key]:
                    raise HarnessError(f"canonical Account fingerprint differs for {arm.label}: {key}")
        else:
            _write_json(self.canonical_path, fingerprint)
        _write_json(
            result_dir / "preparation-record.json",
            {
                "schema_version": 1,
                "arm": arm.label,
                "mode": arm.mode,
                "threshold": arm.threshold,
                "search_type": arm.search_type,
                "resolved": {
                    "uniform_index_count": report["uniform_index_count"],
                    "persisted_index_bytes": report["persisted_index_bytes"],
                    "filter_bytes": report["filter_bytes"],
                    "table_reader_memory_bytes": report["table_reader_memory_bytes"],
                },
                "fingerprint": fingerprint,
            },
        )

    def _cleanup_failed_snapshot(self, paths: tuple[Path, Path, Path]) -> None:
        self._stop_helper()
        self._validate_owned_paths(paths)
        merged = paths[2]
        if self.mount_checker(merged):
            self.unmount(merged)
        if self.mount_checker(merged):
            raise HarnessError(f"failed Account preparation left mounted view: {merged}")
        for path in (paths[0], paths[1], paths[2]):
            if self.mount_checker(path):
                raise HarnessError(f"refusing to remove mounted overlay path {path}")
            if path.exists():
                shutil.rmtree(path)

    def create_snapshot(
        self,
        service: Any,
        name: str,
        source: str,
        original: Callable[[Any, str, str], Path],
    ) -> Path:
        arm = arm_for_snapshot_name(name)
        if self.expected_arm is not None and arm.label != self.expected_arm:
            raise HarnessError(f"EXPB selected arm {arm.label!r}, expected {self.expected_arm!r}")
        paths = self.service_paths(service)
        self._ensure_fresh(paths)
        source_path = Path(source).resolve()
        if source_path == self.scratch_root or _inside(source_path, self.scratch_root):
            raise HarnessError("canonical snapshot source unexpectedly lies in task scratch")
        result_dir = self._result_dir(arm)
        print(f"ACCOUNT_CV_ARM_START label={arm.label} mode={arm.mode} threshold={arm.threshold:g}", flush=True)
        try:
            result = original(service, name, source)
            merged = Path(result).resolve()
            if merged != paths[2] or not merged.is_dir():
                raise HarnessError("EXPB returned a snapshot path outside its owned merged overlay")
            if not self.mount_checker(merged):
                raise HarnessError("EXPB snapshot path is not an active overlay mount")
            (merged / MARKER).write_text("createdbyharness\n", encoding="utf-8")
            self._run_helper(arm, merged, result_dir)
            report = self._validate_helper(arm, result_dir)
            self._record_fingerprint(arm, report, result_dir)
            _write_json(
                result_dir / "snapshot-record.json",
                {
                    "schema_version": 1,
                    "arm": arm.label,
                    "source": str(source_path),
                    "merged": str(merged),
                    "marker": MARKER,
                    "image": self.image,
                    "expb_revision": EXPB_REVISION,
                },
            )
            print(f"ACCOUNT_CV_ARM_PREPARED label={arm.label}", flush=True)
            return result
        except BaseException:
            self._cleanup_failed_snapshot(paths)
            raise

    def delete_snapshot(
        self,
        service: Any,
        name: str,
        source: str,
        original: Callable[[Any, str, str], None],
    ) -> None:
        paths = self.service_paths(service)
        self._validate_owned_paths(paths)
        self._stop_helper()
        # The pinned EXPB implementation removes directories even when umount fails.  Unmount here,
        # verify detachment, and only then let its ordinary cleanup remove the three owned directories.
        if self.mount_checker(paths[2]):
            self.unmount(paths[2])
        if self.mount_checker(paths[2]):
            raise HarnessError(f"refusing EXPB cleanup while overlay remains mounted: {paths[2]}")
        original(service, name, source)
        print(f"ACCOUNT_CV_ARM_END label={arm_for_snapshot_name(name).label}", flush=True)


def _verify_pinned_expb() -> Any:
    try:
        overlay = importlib.import_module("expb.payloads.executor.services.snapshots.overlay")
        service_type = overlay.OverlaySnapshotService
        signature = inspect.signature(service_type.create_snapshot)
        if list(signature.parameters) != ["self", "name", "source"]:
            raise HarnessError(f"unexpected OverlaySnapshotService.create_snapshot signature: {signature}")
        source = inspect.getsource(service_type.create_snapshot)
        for text in ("mount", "overlay_merged_dir", "return self.overlay_merged_dir"):
            if text not in source:
                raise HarnessError(f"pinned EXPB snapshot hook source is missing {text!r}")
        if not hasattr(service_type, "delete_snapshot"):
            raise HarnessError("pinned EXPB snapshot service has no delete_snapshot")
        return service_type
    except ImportError as error:
        raise HarnessError(f"cannot import pinned EXPB: {error}") from error


def install_hook(hook: SnapshotPreparationHook) -> None:
    service_type = _verify_pinned_expb()
    original_create = service_type.create_snapshot
    original_delete = service_type.delete_snapshot
    if getattr(service_type, "_account_cv_hook_installed", False):
        raise HarnessError("Account CV snapshot hook was installed twice")

    def create(service: Any, name: str, source: str) -> Path:
        return hook.create_snapshot(service, name, source, original_create)

    def delete(service: Any, name: str, source: str) -> None:
        return hook.delete_snapshot(service, name, source, original_delete)

    service_type.create_snapshot = create
    service_type.delete_snapshot = delete
    service_type._account_cv_hook_installed = True


def _environment_hook() -> SnapshotPreparationHook:
    scratch = os.environ.get("EXPB_ACCOUNT_SCRATCH_ROOT")
    results = os.environ.get("EXPB_ACCOUNT_RESULTS_ROOT")
    helper = os.environ.get("EXPB_ACCOUNT_HELPER_PATH")
    if not scratch or not results or not helper:
        raise HarnessError("EXPB_ACCOUNT_SCRATCH_ROOT, EXPB_ACCOUNT_RESULTS_ROOT, and EXPB_ACCOUNT_HELPER_PATH are required")
    expected = os.environ.get("EXPB_ACCOUNT_ARM")
    if expected:
        arm_for_label(expected)
    return SnapshotPreparationHook(
        Path(scratch),
        Path(results),
        Path(helper),
        image=os.environ.get("EXPB_ACCOUNT_IMAGE", IMAGE),
        expected_arm=expected,
        helper_cpuset=os.environ.get("EXPB_ACCOUNT_HELPER_CPUSET"),
        helper_memory=os.environ.get("EXPB_ACCOUNT_HELPER_MEMORY"),
    )


def self_test() -> None:
    assert [arm.label for arm in ARMS] == [
        "master",
        "forced-interpolation",
        "auto-cv-0.1",
        "auto-cv-0.2",
        "auto-cv-0.5",
    ]
    assert account_extra_flag(ARMS[0]).endswith("kBinary;block_based_table_factory.uniform_cv_threshold=-1;")
    assert expected_client_container("auto-cv-0.1") == "expb-executor-auto-cv-0-1-nethermind"
    print("expb-account-cv self-test passed")


def main(argv: Sequence[str] | None = None) -> int:
    arguments = list(sys.argv[1:] if argv is None else argv)
    if arguments == ["--self-test"]:
        self_test()
        return 0
    try:
        hook = _environment_hook()
        install_hook(hook)
        from expb import app

        app()
        return 0
    except HarnessError as error:
        print(f"expb-account-cv: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
