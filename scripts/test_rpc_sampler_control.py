# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""The sampler stops on owned-pipe EOF, including an already reaped child."""
import importlib.util
import io
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).parent / "rpc-bench"
SPEC = importlib.util.spec_from_file_location("sample_resources_control", HERE / "sample-resources.py")
SAMPLER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SAMPLER)
BASH = (str(Path("C:/Program Files/Git/bin/bash.exe")) if os.name == "nt" and Path("C:/Program Files/Git/bin/bash.exe").is_file()
        else shutil.which("bash"))


class SamplerControlTests(unittest.TestCase):
    def test_control_checks_eof_without_blocking_a_live_pipe(self):
        for ready, value, expected in ((False, b"", False), (True, b"x", False), (True, b"", True)):
            with self.subTest(ready=ready, value=value):
                stdin = unittest.mock.Mock(buffer=io.BytesIO(value))
                with patch.object(SAMPLER.sys, "stdin", stdin), patch.object(SAMPLER.select, "select", return_value=([stdin] if ready else [], [], [])):
                    self.assertEqual(SAMPLER._control_closed(), expected)

    @unittest.skipUnless(BASH, "requires Bash coprocess pipes")
    def test_bash_coprocess_eof_and_reaped_child_never_signal_pid(self):
        source = (HERE / "run-jsonbench.sh").read_text()
        start = source.index("# Resource sampling brackets container execution only.\n")
        fragment = source[start:source.index("\ntool_failed=0\n", start)]
        self.assertNotIn("kill ", fragment)
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            fixture = directory / "python3"
            fixture.write_text('''#!/usr/bin/env bash
if [[ "$EARLY_EXIT" == "true" ]]; then exit 0; fi
while IFS= read -r control; do :; done
printf 'closed\\n' > "$RESOURCE_SAMPLER_OUT"
''', newline="\n")
            fixture.chmod(0o700)
            # The stand-in blocks on the same owned control FD as the real sampler.
            for early in ("false", "true"):
                with self.subTest(early=early):
                    out = directory / ("shell-eof-" + early)
                    script = directory / ("run-" + early + ".sh")
                    script.write_text('set -euo pipefail\nPATH="$HERE:$PATH"\n' + fragment +
                                      '\nsleep 0.1\nstop_resource_sampler\nstop_resource_sampler\n', newline="\n")
                    result = subprocess.run([BASH, str(script)], env=os.environ | {"HERE": str(directory),
                        "RESOURCE_SAMPLER_CONTAINER": "fixture", "RESOURCE_SAMPLER_OUT": str(out), "EARLY_EXIT": early},
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=5, check=False)
                    self.assertEqual(result.returncode, 0, result.stderr.decode())
                    self.assertEqual(out.exists(), early == "false")

    @unittest.skipUnless(os.name == "posix" and shutil.which("bash"), "requires native POSIX pipes and Bash")
    def test_real_control_pipe_closes_only_at_eof(self):
        read_fd, write_fd = os.pipe()
        with os.fdopen(read_fd, "rb") as pipe:
            with patch.object(SAMPLER.sys, "stdin", unittest.mock.Mock(buffer=pipe, fileno=pipe.fileno)):
                self.assertFalse(SAMPLER._control_closed())
                os.close(write_fd)
                self.assertTrue(SAMPLER._control_closed())

    @unittest.skipUnless(os.name == "posix" and shutil.which("bash"), "requires native POSIX pipes and Bash")
    def test_shell_stop_waits_own_child_and_is_idempotent_after_early_exit(self):
        source = (HERE / "run-jsonbench.sh").read_text()
        start = source.index("# Resource sampling brackets container execution only.\n")
        fragment = source[start:source.index("\ntool_failed=0\n", start)]
        self.assertNotIn("kill ", fragment)
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            # Execute the actual CLI/control predicate against deterministic cgroup counters.
            loader = '''import importlib.util, os, sys
from pathlib import Path
spec=importlib.util.spec_from_file_location("sampler",os.environ["SAMPLER_SOURCE"])
module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
if os.environ["EARLY_EXIT"] == "true":
    module._container_id=lambda _: (_ for _ in ()).throw(module.ResourceSampleError("fixture unavailable"))
else:
    module._container_id=lambda _: "fixture"
    module._cgroup_dir=lambda _: Path(os.environ["CGROUP_FIXTURE"])
raise SystemExit(module.main())
'''
            (directory / "sample-resources.py").write_text(loader)
            (directory / "cpu.stat").write_text("usage_usec 1000\nthrottled_usec 0\n")
            (directory / "memory.current").write_text("1024\n")
            (directory / "memory.stat").write_text("anon 512\nfile 512\n")
            (directory / "io.stat").write_text("")
            for early in ("false", "true"):
                with self.subTest(early=early):
                    out = directory / ("resources-" + early + ".json")
                    environment = os.environ | {"HERE": str(directory), "SAMPLER_SOURCE": str(HERE / "sample-resources.py"),
                        "CGROUP_FIXTURE": str(directory), "RESOURCE_SAMPLER_CONTAINER": "fixture",
                        "RESOURCE_SAMPLER_OUT": str(out), "EARLY_EXIT": early}
                    command = "set -euo pipefail\n" + fragment + "\nsleep 0.2\nstop_resource_sampler\nstop_resource_sampler\n"
                    result = subprocess.run(["bash", "-c", command], env=environment, stdout=subprocess.PIPE,
                                            stderr=subprocess.PIPE, timeout=5, check=False)
                    self.assertEqual(result.returncode, 0, result.stderr.decode())
                    self.assertEqual(out.exists(), early == "false")


if __name__ == "__main__":
    unittest.main()
