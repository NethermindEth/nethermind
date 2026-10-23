import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from embedded import CASES


class RuntimeBuildTests(unittest.TestCase):
    def test_selected_tests_build_with_missing_or_prebuilt_runtime_and_failures_stop_publication(self):
        script = Path(__file__).with_name("build-runtime.sh")
        selected = list(CASES) + ["nethermind_streaming_controls"]
        for prebuilt in (False, True):
            for failed in (False, True):
                with self.subTest(prebuilt=prebuilt, failed=failed), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    upstream, output, commands = [root / name for name in ("source", "output", "commands")]
                    runtime = output / "runtime"
                    for path in (upstream, runtime, commands): path.mkdir(parents=True)
                    for name in ("cargo", "python3"):
                        command = commands / name
                        command.write_text("#!" + sys.executable + "\n" + DRIVER)
                        command.chmod(0o755)
                    composer = runtime / "eez-composer"
                    if prebuilt:
                        composer.write_text("trusted imported runtime")
                        composer.chmod(0o555)
                    environment = {**os.environ, "PATH": str(commands) + os.pathsep + os.environ["PATH"],
                        "EEZ_PROFILE": "rollup0", "EEZ_UPSTREAM": str(upstream), "EEZ_CI_RESULT_DIR": str(output),
                        "EEZ_RUNTIME_DIR": str(runtime), "EEZ_BUILD_CALLS": str(root / "calls.jsonl"),
                        "EEZ_BUILD_FAIL": str(int(failed)), "GITHUB_WORKSPACE": str(script.parents[3]),
                        "GITHUB_PATH": str(root / "github-path")}
                    result = subprocess.run(["bash", str(script)], env=environment, capture_output=True, text=True, timeout=15)
                    calls = [json.loads(line) for line in (root / "calls.jsonl").read_text().splitlines()]
                    builds = [call for call in calls if call[0] == "cargo"]
                    self.assertEqual(len(builds), 1, "selected tests require one Cargo invocation even with prebuilt runtime")
                    cargo = builds[0]
                    self.assertEqual(cargo[1], "build")
                    self.assertIn("--locked", cargo)
                    self.assertIn("--timings", cargo)
                    self.assertEqual(cargo[cargo.index("--profile") + 1], "ci-e2e")
                    self.assertCountEqual([cargo[i + 1] for i, value in enumerate(cargo) if value == "--test"], selected)
                    packages = [cargo[i + 1] for i, value in enumerate(cargo) if value == "-p"]
                    self.assertEqual(packages, ["eez-node-e2e"] + ([] if prebuilt else ["eez-node", "eez-follower", "eez-prover-stateless"]))
                    self.assertEqual("--bins" in cargo, not prebuilt)
                    self.assertEqual("--examples" in cargo, not prebuilt)
                    self.assertEqual(Path(calls[0][1]).name, "embedded.py")
                    self.assertEqual(Path(calls[1][1]).name, "controls.py")
                    self.assertEqual(calls[2], cargo, "observation adapters must be installed before compilation")
                    self.assertEqual(result.returncode, 1 if failed else 0, result.stderr)
                    if failed:
                        self.assertEqual(calls[-1], cargo, "failed compilation must stop inventory and runtime publication")
                        self.assertFalse((root / "github-path").exists())
                        if not prebuilt: self.assertEqual(list(runtime.iterdir()), [])
                    else:
                        self.assertEqual((output / "cargo-timing.html").read_text(), "compiler timing fixture")
                        self.assertEqual(Path(calls[3][1]).name, "bundle.py")
                        self.assertEqual(calls[3][2], "tests")
                        self.assertEqual((root / "github-path").read_text().strip(), str(runtime))
                        records = [call for call in calls if Path(call[1]).name == "upstream.py"]
                        self.assertEqual(len(records), 0 if prebuilt else 1)
                        if prebuilt:
                            self.assertEqual(composer.read_text(), "trusted imported runtime")
                        else:
                            self.assertEqual(sorted(path.name for path in runtime.iterdir()),
                                sorted(["eez-composer", "eez-follower", "eez-proof-signer", "eez-genesis-state-root", "eez-genesis-block-hash"]))


DRIVER = '''
import json
import os
from pathlib import Path
import sys

name = Path(sys.argv[0]).name
with Path(os.environ["EEZ_BUILD_CALLS"]).open("a") as stream:
    stream.write(json.dumps([name, *sys.argv[1:]]) + "\\n")
if name == "cargo":
    if os.environ["EEZ_BUILD_FAIL"] == "1": sys.exit(1)
    timing = Path(os.environ["EEZ_UPSTREAM"]) / "target/cargo-timings/cargo-timing.html"
    timing.parent.mkdir(parents=True, exist_ok=True)
    timing.write_text("compiler timing fixture")
    if "--bins" in sys.argv:
        for relative in ("eez-composer", "eez-follower", "eez-proof-signer", "examples/genesis_state_root", "examples/genesis_block_hash"):
            path = Path(os.environ["EEZ_UPSTREAM"]) / "target/ci-e2e" / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("compiled runtime fixture")
            path.chmod(0o555)
'''
