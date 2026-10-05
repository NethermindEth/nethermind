import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest


HERE = Path(__file__).resolve().parent
SOURCE = Path(os.environ.get("RPC_PERMISSION_SOURCE", str(HERE / "rpc-bench")))
WRAPPER = '''#!/usr/bin/env python3
import json
import os
from pathlib import Path

scratch = Path(os.environ["SCRATCH_ROOT"])
work = scratch / "jsonbench"
fixtures = work / "src/rpc-calls/corpus"
fixtures.mkdir(parents=True)
fixture = fixtures / "class_1.json"
fixture.write_bytes(b"[]\\n")
fixture.chmod(0o644)
raw = work / "io/out"
raw.mkdir(parents=True)
summary = {"metrics": {"http_reqs": {"values": {"count": 6000}},
    "http_req_failed": {"values": {"rate": 0}},
    "checks": {"values": {"fails": 0, "passes": 12000}}}}
for target in [raw / "summary.json", Path(os.environ["OUT_DIR"]) / "summary.json"]:
    target.write_text(json.dumps(summary))
(work / "jsonbench-tool.log").write_text("synthetic fixture prepared\\n")
warm = Path(os.environ["RPC_TEST_WARM_DIR"])
(warm / "runtime-pin-complete.json").write_text('{"complete":true}')
print("synthetic fixture prepared")
'''
READER = '''import errno, os, sys
fixture_root, archive_root = map(int, sys.argv[1:])
try:
    os.open("preparation.json", os.O_RDONLY, dir_fd=archive_root)
except OSError as error:
    if error.errno != errno.EACCES:
        raise
else:
    raise AssertionError("UID1001 could read private archive metadata")
with os.fdopen(os.open("rpc-calls/corpus/class_1.json", os.O_RDONLY,
                      dir_fd=fixture_root), "rb") as source:
    assert source.read() == b"[]\\n"
'''


def identity(path):
    info = path.lstat()
    return [info.st_dev, info.st_ino, stat.S_IFMT(info.st_mode), info.st_uid]


@unittest.skipUnless(sys.platform == "linux" and os.geteuid() == 0,
                     "Linux root required for an actual UID1001 permission check")
class CliFixturePermissionsTests(unittest.TestCase):
    def test_cli_inherited077_allows_uid1001_fixture_but_keeps_archive_private(self):
        with tempfile.TemporaryDirectory(prefix="rpc-cli-permissions-") as temporary:
            base = Path(temporary)
            scripts = base / "scripts"
            shutil.copytree(SOURCE, scripts)
            wrapper = scripts / "run-jsonbench.sh"
            wrapper.write_text(WRAPPER)
            wrapper.chmod(0o700)
            storage = base / "rpc-private-123-1"
            storage.mkdir(mode=0o700)
            roots = {name: storage / child for name, child in
                     (("SCRATCH_ROOT", "scratch"), ("OUT_DIR", "out"), ("STATE_ROOT", "state"))}
            for path in roots.values():
                path.mkdir(mode=0o700)
            archive = storage / "spin-diagnostic-123-1"
            archive.mkdir(mode=0o700)
            (archive / "outputs").mkdir(mode=0o700)
            preparation = {"outputs": str(archive / "outputs"), "purpose": "RPC_PRIVATE_AUDIT",
                "storage_identity": identity(storage), "roots": {key: str(path) for key, path in roots.items()},
                "root_identities": {key: identity(path) for key, path in roots.items()},
                "expected_counts": {"warm": 6000}}
            (archive / "preparation.json").write_text(json.dumps(preparation))
            cell = roots["SCRATCH_ROOT"] / "warmup-cell/default/nativecap"
            cell.mkdir(parents=True, mode=0o700)
            warm = archive / "outputs/nativecap/warm"
            environment = {key: value for key, value in os.environ.items()
                           if key not in ("GH_TOKEN", "GITHUB_TOKEN")}
            environment.update({key: str(path) for key, path in roots.items()})
            environment.update(RPC_PRIVATE_STORAGE_ROOT=str(storage), GITHUB_RUN_ID="123",
                GITHUB_RUN_ATTEMPT="1", RPC_PRIVATE_AUDIT_PHASE="warm", LABEL="nativecap",
                OUT_DIR=str(cell), RPC_TEST_WARM_DIR=str(warm))
            cli = subprocess.run([sys.executable, "-B", str(scripts / "private_audit.py"), "cell"],
                env=environment, umask=0o077, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                timeout=20, check=False)
            self.assertEqual(cli.returncode, 0, "synthetic CLI cell did not complete")
            self.assertTrue(json.loads((warm / "audit.json").read_text())["complete"])
            for directory in (storage, archive, archive / "outputs", warm):
                self.assertEqual(stat.S_IMODE(directory.stat().st_mode), 0o700)
            for name in ("audit.json", "wrapper.log", "tool.log", "summary.raw.json", "summary.json"):
                self.assertEqual(stat.S_IMODE((warm / name).stat().st_mode), 0o600)
            fixture_root = roots["SCRATCH_ROOT"] / "jsonbench/src"
            descriptors = [os.open(path, os.O_RDONLY | os.O_DIRECTORY)
                           for path in (fixture_root, archive)]
            try:
                # A bind mount starts lookup at its source inode, excluding private host ancestors.
                reader = subprocess.run([sys.executable, "-B", "-c", READER,
                    *(str(fd) for fd in descriptors)], pass_fds=descriptors, user=1001, group=1001,
                    extra_groups=(), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                    timeout=20, check=False)
                self.assertEqual(reader.returncode, 0,
                    "UID1001 must traverse/read fixture through its bound root while archive access is denied")
            finally:
                for descriptor in descriptors:
                    os.close(descriptor)


if __name__ == "__main__":
    unittest.main()
