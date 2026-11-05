import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from partitions import adapt_driver, defer_native_settlement


class NativeReadinessTests(unittest.TestCase):
    def test_scenario_creates_settlement_before_required_canonical_check_and_observations(self):
        for selected in ("verify-cross-chain-waves", "run-protocol-e2e"):
            for failure in (None, "scenario", "canonical"):
                with self.subTest(selected=selected, failure=failure), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    scripts = root / "scripts"
                    scripts.mkdir()
                    (scripts / (selected + ".sh")).write_text(
                        'echo scenario >> "$HERE/events"\n'
                        '[[ "${FAILURE:-}" != scenario ]] || exit 7\n'
                        'if [[ "${FAILURE:-}" == canonical ]]; then echo no-settlement > "$HERE/canonical"; else '
                        'echo \'{"event_name":"eez.deriver.safe.advanced"}\' > "$HERE/canonical"; fi\n')
                    observe = root / "observe.sh"
                    observe.write_text('echo "observe:$1" >> "$HERE/events"\n')
                    driver = self.driver(selected)
                    adapted = defer_native_settlement(driver, selected)
                    result = subprocess.run(["bash", "-c", adapted], capture_output=True, text=True, timeout=10,
                        env={**os.environ, "HERE": str(root), "NETHERMIND_OBSERVE_SCRIPT": str(observe),
                             "FAILURE": failure or ""})
                    events = (root / "events").read_text().splitlines()
                    if failure is None:
                        self.assertEqual(result.returncode, 0, result.stderr)
                        self.assertEqual(events, ["scenario", "canonical", "observe:verify-eezl2-deployment",
                                                   "observe:" + selected])
                    else:
                        self.assertNotEqual(result.returncode, 0)
                        self.assertEqual(events, ["scenario"] if failure == "scenario" else ["scenario", "canonical"])
                    self.assertEqual(driver.count('grep -Fq'), adapted.count('grep -Fq'))
                    self.assertEqual(driver.count('bash "$HERE/scripts/'), adapted.count('bash "$HERE/scripts/'))

    def test_native_caller_uses_real_inbound_for_protocol_and_preserves_other_profiles(self):
        names = ["verify-eezl2-deployment", "verify-cross-chain-waves", "run-protocol-e2e"]
        readiness = '# Canonical replay proves readiness; an observer receipt alone does not.\nrequired_canonical_check\n'
        for profile, suite in (("rollupx", "protocol"), ("rollupx", "wave-mixed"),
                               ("rollupx", "recovery"), ("rollup0", "protocol")):
            with self.subTest(profile=profile, suite=suite), tempfile.TemporaryDirectory() as directory:
                inventory = names + (["verify-ingress-admission", "verify-fault-recovery"] if profile == "rollup0" else [])
                commands = ('write_result() { :; }\ncleanup() {\n    local status=$?\n'
                            '    bash "$HERE/stop.sh" || true\n    exit "$status"\n}\ntrap cleanup EXIT\n' + readiness)
                commands += 'echo "==> running native cross-chain wave suite"\n'
                for name in inventory:
                    commands += ('bash "$HERE/scripts/' + name + '.sh"\n'
                                 'bash "$NETHERMIND_OBSERVE_SCRIPT" ' + name + '\n')
                commands += 'bash "$NETHERMIND_OBSERVE_SCRIPT" recovery\n'
                commands += 'if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then\n :\nfi\n'
                with patch("partitions.adapt_waves"):
                    adapted, hooks = adapt_driver(commands, Path(directory), profile, suite)
                selected = "verify-cross-chain-waves" if suite in ("wave-mixed", "recovery") else "run-protocol-e2e"
                call = 'bash "$HERE/scripts/' + selected + '.sh"'
                gate = adapted.index("required_canonical_check")
                if profile == "rollupx" and suite == "wave-mixed":
                    self.assertLess(adapted.index(call), gate)
                    self.assertLess(gate, adapted.index('bash "$NETHERMIND_OBSERVE_SCRIPT" ' + selected))
                else:
                    self.assertLess(gate, adapted.index(call))
                inbound = ('timed_phase settlement-inbound env EEZ_WAVE_MODE=inbound EEZ_WAVE_COUNT=1 '
                           'EEZ_INCLUDE_REVERTS=1 bash "$HERE/scripts/cross-chain-wave.sh"')
                if profile == "rollupx" and suite == "protocol":
                    self.assertEqual(adapted.count(inbound), 1)
                    self.assertLess(adapted.index(inbound), gate)
                else:
                    self.assertNotIn(inbound, adapted)
                self.assertEqual(hooks, ["verify-eezl2-deployment", selected])

    def test_changed_or_ambiguous_upstream_boundaries_are_rejected(self):
        selected = "run-protocol-e2e"
        source = self.driver(selected)
        start = '# Canonical replay proves readiness; an observer receipt alone does not.\n'
        end = '\necho "==> running native cross-chain wave suite"'
        call = 'timed_phase selected bash "$HERE/scripts/' + selected + '.sh"'
        mutations = [source.replace(start, ""), source + start, source.replace(end, ""), source + end,
                     source.replace(call, ""), source + "\n" + call,
                     source.replace(start, "START").replace(end, start).replace("START", end)]
        for source in mutations:
            with self.subTest(source=source), self.assertRaises(ValueError):
                defer_native_settlement(source, selected)

    @staticmethod
    def driver(selected):
        return '''set -euo pipefail
timed_phase() { shift; "$@"; }
kurtosis() { echo canonical >> "$HERE/events"; cat "$HERE/canonical"; }
# Canonical replay proves readiness; an observer receipt alone does not.
node_logs="$(kurtosis)"
[[ $readiness_started -le $SECONDS && $deadline -gt $SECONDS ]]
grep -Fq '"event_name":"eez.deriver.safe.advanced"' <<< "$node_logs"

echo "==> running native cross-chain wave suite"
timed_phase selected bash "$HERE/scripts/''' + selected + '''.sh"
bash "$NETHERMIND_OBSERVE_SCRIPT" verify-eezl2-deployment
bash "$NETHERMIND_OBSERVE_SCRIPT" ''' + selected + '\n'
