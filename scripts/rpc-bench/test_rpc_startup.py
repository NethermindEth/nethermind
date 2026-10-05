import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent
CID = '5d2b600363359dcda9ae92ef8a1228246ae014b35350361b90a6ab9544491e39'
BASH = os.environ.get('TEST_BASH') or shutil.which('bash')
LIB = Path(os.environ.get('TEST_LIB', str(ROOT / 'lib.sh'))).resolve()

SCRIPT = r'''
set -euo pipefail
source "$TEST_LIB"
docker() {
  printf '%s\n' "$*" >> "$TRACE"
  case "$1" in
    container)
      [[ "$#" == 5 && "$2" == inspect && "$3" == --format && "$4" == '{{.Id}} {{.State.Running}}' && "$5" == "$TARGET" ]] || return 91
      if [[ "$MODE" == dies && -f "$TRACE.seen" ]]; then
        printf '%s false\n' "$TARGET"
      else
        printf '%s\n' "$INSPECT"
      fi
      touch "$TRACE.seen"
      return "$INSPECT_RC"
      ;;
    ps) printf '%s\n' "$NAMES" ;;
    logs) [[ "$2" == "$TARGET" ]] || return 92 ;;
    *) return 93 ;;
  esac
}
rpc_head() { printf 'rpc\n' >> "$TRACE"; printf '%s\n' "$HEAD"; }
sleep() { printf 'sleep\n' >> "$TRACE"; }
wait_for_rpc http://localhost:8545 5 "$TARGET"
'''

class StartupTests(unittest.TestCase):
    def run_case(self, target=CID, inspect=None, inspect_rc=0, names='rpcbench-owned',
                 head='0x1845418', mode='', timeout=5):
        with tempfile.TemporaryDirectory() as tmp:
            trace = Path(tmp) / 'trace'
            env = os.environ.copy()
            env.update(TEST_LIB=LIB.as_posix(), TRACE=trace.as_posix(), TARGET=target,
                       INSPECT=inspect if inspect is not None else CID + ' true',
                       INSPECT_RC=str(inspect_rc), NAMES=names, HEAD=head, MODE=mode)
            script = SCRIPT.replace('8545 5 "$TARGET"', '8545 ' + str(timeout) + ' "$TARGET"')
            result = subprocess.run([BASH, '--noprofile', '--norc', '-c', script],
                                    env=env, text=True, capture_output=True, timeout=10)
            calls = trace.read_text().splitlines() if trace.exists() else []
            return result, calls

    def test_owned_cid_ready_even_though_docker_lists_only_name(self):
        result, calls = self.run_case()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('JSON-RPC is up', result.stdout)
        self.assertEqual(1, calls.count('rpc'))
        self.assertFalse(any(c.startswith('ps ') for c in calls))
        self.assertTrue(calls[0].endswith(CID))

    def test_cid_state_and_identity_fail_closed(self):
        for state, rc in [(CID + ' false', 0), ('a' * 64 + ' true', 0), ('', 0),
                          (CID + ' true', 1), (CID[:12] + ' true', 0),
                          (CID + ' true\n' + 'a' * 64 + ' true', 0)]:
            with self.subTest(state=state, rc=rc):
                result, calls = self.run_case(inspect=state, inspect_rc=rc, names=CID)
                self.assertNotEqual(0, result.returncode)
                self.assertIn('node container died', result.stderr)
                self.assertNotIn('rpc', calls)
                self.assertIn('logs ' + CID, calls)
                self.assertFalse(any(c.startswith('ps ') for c in calls))

    def test_public_names_keep_existing_behavior(self):
        for name in ['rpcbench-primary', 'rpcbench-reference']:
            for present in [True, False]:
                with self.subTest(name=name, present=present):
                    result, calls = self.run_case(target=name, names=name if present else 'different')
                    self.assertEqual(present, result.returncode == 0)
                    self.assertTrue(calls[0].startswith('ps '))
                    self.assertFalse(any(c.startswith('container ') for c in calls))

    def test_no_container_keeps_rpc_only_behavior(self):
        result, calls = self.run_case(target='')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(['rpc'], calls)

    def test_each_poll_rechecks_same_cid_before_rpc(self):
        result, calls = self.run_case(head='', mode='dies', timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('node container died', result.stderr)
        self.assertEqual(1, calls.count('rpc'))
        inspections = [c for c in calls if c.startswith('container ')]
        self.assertEqual(2, len(inspections))
        self.assertTrue(all(c.endswith(CID) for c in inspections))

    def test_rpc_genesis_and_timeout_still_fail(self):
        for head, message in [('0x0', 'head block 0'), ('', 'did not become ready')]:
            with self.subTest(head=head):
                result, calls = self.run_case(head=head)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message, result.stderr)

if __name__ == '__main__':
    unittest.main(verbosity=2)
