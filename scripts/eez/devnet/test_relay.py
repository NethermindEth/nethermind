import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class RelayReadinessTests(unittest.TestCase):
    def test_refresh_requires_actual_registrations_and_successful_restart(self):
        for failure in (None, "registrations", "builder", "stop", "start"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                tool = root / "kurtosis"
                tool.write_text('''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
args = sys.argv[1:]
with Path(os.environ["TRACE"]).open("a") as trace:
    trace.write(json.dumps(args) + "\\n")
if args[:3] == ["service", "logs", "--all"]:
    if args[4] == "mev-relay-api":
        print(os.environ["REGISTRATIONS"])
    elif args[4] == "builder":
        print(os.environ["BUILDER"])
    else:
        sys.exit(42)
elif args[:2] in (["service", "stop"], ["service", "start"]):
    if args[1] == os.environ["FAIL_ACTION"]:
        sys.exit(7)
else:
    sys.exit(42)
''')
                tool.chmod(0o755)
                trace = root / "trace.jsonl"
                environment = dict(os.environ, PATH=str(root) + os.pathsep + os.environ["PATH"],
                    KURTOSIS_ENCLAVE="fixture", KURTOSIS_BUILDER_SERVICE="builder",
                    EEZ_RELAY_READY_TIMEOUT_SECS="0", TRACE=str(trace),
                    REGISTRATIONS="validator registrations call processed numRegistrationsNew=" + ("0" if failure == "registrations" else "8"),
                    BUILDER="Updated validator registrations len=" + ("0" if failure == "builder" else "8"),
                    FAIL_ACTION=failure or "")
                result = subprocess.run(["bash", str(Path(__file__).with_name("relay.sh"))],
                    env=environment, capture_output=True, text=True, timeout=10)
                calls = [json.loads(line) for line in trace.read_text().splitlines()]
                self.assertEqual(failure is None, result.returncode == 0, result.stderr)
                expected = [["service", "logs", "--all", "fixture", "mev-relay-api"]]
                if failure != "registrations":
                    expected.append(["service", "stop", "fixture", "mev-relay-housekeeper"])
                if failure not in ("registrations", "stop"):
                    expected.append(["service", "start", "fixture", "mev-relay-housekeeper"])
                if failure not in ("registrations", "stop", "start"):
                    expected.append(["service", "logs", "--all", "fixture", "builder"])
                self.assertEqual(expected, calls)
