#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression tests for the non-destructive ARM storage guard."""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "scripts" / "rpc-bench" / "check-storage-space.sh"
GIB = 1024 ** 3


def find_bash() -> str | None:
    if os.name == "nt":
        git_bash = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Git" / "bin" / "bash.exe"
        if git_bash.is_file():
            return str(git_bash)
    return shutil.which("bash")


def find_cygpath(bash: str | None) -> str | None:
    if os.name != "nt":
        return None
    found = shutil.which("cygpath")
    if found:
        return found
    return str(Path(bash).parents[1] / "usr" / "bin" / "cygpath.exe") if bash else None


BASH = find_bash()
CYGPATH = find_cygpath(BASH)


@unittest.skipUnless(BASH and (os.name != "nt" or CYGPATH), "bash and path conversion are required")
class StorageSpaceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.paths = {
            "root": self.root / "root",
            "temp": self.root / "runner-temp",
            "containerd": self.root / "containerd",
            "docker": self.root / "docker",
            "scratch_parent": self.root / "scratch-parent",
        }
        for path in self.paths.values():
            path.mkdir()
        self._write_stubs()

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def _bash_path(self, path: Path) -> str:
        if os.name != "nt":
            return str(path)
        result = subprocess.run(
            [CYGPATH, "-u", str(path)], check=True, capture_output=True, text=True)
        return result.stdout.strip()

    def _write_stubs(self) -> None:
        (self.bin / "docker").write_bytes((
            "#!/usr/bin/env bash\n"
            "if [[ \"${DOCKER_FAIL:-0}\" == 1 ]]; then\n"
            "  [[ \"${DOCKER_FAIL_VALID:-0}\" == 1 ]] && printf '%s\\n' \"${DOCKER_ROOT:-}\"\n"
            "  exit 1\n"
            "fi\n"
            "[[ \"$1\" == info ]] || exit 1\n"
            "printf '%s\\n' \"${DOCKER_ROOT:-}\"\n"
        ).encode())
        (self.bin / "df").write_bytes((
            "#!/usr/bin/env bash\n"
            "path=\"${!#}\"\n"
            "case \"${path}\" in\n"
            "  \"${ROOT_PATH}\") value=\"${DF_ROOT:-}\" ;;\n"
            "  \"${RUNNER_TEMP}\") value=\"${DF_TEMP:-}\" ;;\n"
            "  \"${CONTAINERD_PATH}\") value=\"${DF_CONTAINERD:-}\" ;;\n"
            "  \"${DOCKER_ROOT}\") value=\"${DF_DOCKER:-}\" ;;\n"
            "  \"${SCRATCH_PARENT}\") value=\"${DF_SCRATCH:-}\" ;;\n"
            "  *) exit 1 ;;\n"
            "esac\n"
            "if [[ \"${DF_ERROR_PATH:-}\" == \"${path}\" ]]; then exit 1; fi\n"
            "if [[ \"${DF_FAIL_VALID_PATH:-}\" == \"${path}\" ]]; then printf 'Avail\\n%s\\n' \"${value}\"; exit 1; fi\n"
            "if [[ \"${DF_MALFORMED_PATH:-}\" == \"${path}\" ]]; then printf 'Avail\\n%s junk\\n' \"${value}\"; exit 0; fi\n"
            "printf 'Avail\\n%s\\n' \"${value}\"\n"
        ).encode())
        for stub in (self.bin / "docker", self.bin / "df"):
            stub.chmod(0o755)

    def _base_environment(self) -> dict[str, str]:
        root = {key: self._bash_path(value) for key, value in self.paths.items()}
        environment = os.environ.copy()
        environment.update({
            "ARCH": "arm64",
            "ROOT_PATH": root["root"],
            "RUNNER_TEMP": root["temp"],
            "CONTAINERD_PATH": root["containerd"],
            "SCRATCH_ROOT": f"{root['scratch_parent']}/not-created-yet",
            "SCRATCH_PARENT": root["scratch_parent"],
            "DOCKER_ROOT": root["docker"],
            "DF_COMMAND": self._bash_path(self.bin / "df"),
            "DF_ROOT": str(2 * GIB),
            "DF_TEMP": str(2 * GIB),
            "DF_CONTAINERD": str(7 * GIB),
            "DF_DOCKER": str(7 * GIB),
            "DF_SCRATCH": str(7 * GIB),
            "DOCKER_FAIL": "0",
            "DF_ERROR_PATH": "",
            "DF_MALFORMED_PATH": "",
        })
        environment["PATH"] = f"{self._bash_path(self.bin)}:{environment.get('PATH', '')}"
        return environment

    def _run(self, updates: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
        environment = self._base_environment()
        environment.update(updates or {})
        return subprocess.run(
            [BASH, self._bash_path(HELPER), "pre-pull"],
            check=False,
            capture_output=True,
            text=True,
            env=environment,
        )

    def test_healthy_separate_mounts_and_missing_scratch_ancestor_pass(self) -> None:
        result = self._run()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("nearest existing ancestor", result.stdout)

    def test_low_root_fails_small_reserve(self) -> None:
        result = self._run({"DF_ROOT": str(GIB - 1)})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("root filesystem", result.stderr)

    def test_low_bulk_path_fails_six_gib_reserve(self) -> None:
        result = self._run({"DF_CONTAINERD": str(6 * GIB - 1)})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("containerd storage", result.stderr)

    def test_bulk_storage_on_root_still_uses_six_gib_reserve(self) -> None:
        result = self._run({
            "DF_ROOT": str(5 * GIB),
            "DF_TEMP": str(5 * GIB),
            "DF_CONTAINERD": str(5 * GIB),
            "DF_DOCKER": str(5 * GIB),
            "DF_SCRATCH": str(5 * GIB),
        })
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("containerd storage", result.stderr)

    def test_df_error_and_non_numeric_readings_fail_closed(self) -> None:
        root_path = self._bash_path(self.paths["root"])
        result = self._run({"DF_ERROR_PATH": root_path})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("df did not return numeric", result.stderr)

        result = self._run({"DF_DOCKER": "not-a-number"})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("DockerRootDir", result.stderr)

        result = self._run({"DF_FAIL_VALID_PATH": root_path})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("df did not return numeric", result.stderr)

        result = self._run({"DF_MALFORMED_PATH": root_path})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("df did not return numeric", result.stderr)

    def test_docker_nonzero_with_valid_output_fails_closed(self) -> None:
        result = self._run({"DOCKER_FAIL": "1", "DOCKER_FAIL_VALID": "1"})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("DockerRootDir is missing or invalid", result.stderr)

    def test_docker_failure_fails_closed(self) -> None:
        result = self._run({"DOCKER_FAIL": "1"})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("DockerRootDir is missing or invalid", result.stderr)

        result = self._run({"DOCKER_ROOT": "relative-docker-root"})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("DockerRootDir is missing or invalid", result.stderr)


if __name__ == "__main__":
    unittest.main()
