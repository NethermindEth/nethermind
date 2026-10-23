import copy
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import partitions
import prepare


class PartitionTests(unittest.TestCase):
    def test_partition_captures_genesis_then_runs_scenario_before_full_prefix(self):
        first = 'bash "$NETHERMIND_OBSERVE_SCRIPT" verify-eezl2-deployment'
        scenario = 'bash "$HERE/scripts/run-protocol-e2e.sh"'
        selected = 'bash "$NETHERMIND_OBSERVE_SCRIPT" run-protocol-e2e'
        source = first + "\n" + scenario + "\n" + selected
        changed = prepare.partition_observations(source, ["verify-eezl2-deployment", "run-protocol-e2e"])
        genesis = 'bash "$NETHERMIND_OBSERVE_SCRIPT" genesis'
        self.assertLess(changed.index(genesis), changed.index(scenario))
        self.assertLess(changed.index(scenario), changed.index(first))
        self.assertLess(changed.index(first), changed.index(selected))
        for value, names in ((source.replace(first, ""), ["initial", "run-protocol-e2e"]),
                             (source + selected, ["initial", "run-protocol-e2e"]), (source, ["initial"])):
            with self.subTest(source=value, names=names), self.assertRaises(ValueError):
                prepare.partition_observations(value, names)

    def test_driver_keeps_selected_script_and_removes_only_other_suite_calls(self):
        driver = self.driver()
        with tempfile.TemporaryDirectory() as directory:
            changed, hooks = partitions.adapt_driver(driver, Path(directory), "rollup0", "ingress")
        self.assertEqual(["verify-eezl2-deployment", "verify-ingress-admission"], hooks)
        self.assertIn('bash "$HERE/scripts/verify-ingress-admission.sh"', changed)
        self.assertNotIn('bash "$HERE/scripts/run-protocol-e2e.sh"', changed)
        self.assertNotIn('bash "$NETHERMIND_OBSERVE_SCRIPT" recovery', changed)
        self.assertNotIn("misleading-full-pass", changed)
        with self.assertRaises(AssertionError):
            partitions.adapt_driver(driver + 'bash "$NETHERMIND_OBSERVE_SCRIPT" new-suite\n', Path("unused"), "rollup0", "ingress")
        with self.assertRaises(ValueError):
            partitions.adapt_driver(driver.replace('bash "$HERE/scripts/verify-ingress-admission.sh"', ""), Path("unused"), "rollup0", "ingress")

    def test_driver_retains_cleanup_and_failure_status_but_leaves_teardown_to_outer_wrapper(self):
        for status in (0, 7):
            with self.subTest(status=status), tempfile.TemporaryDirectory() as directory:
                changed, _ = partitions.adapt_driver(self.driver(), Path(directory), "rollup0", "ingress")
                result = subprocess.run(["bash", "-c", changed], capture_output=True, text=True, timeout=10,
                    env={**os.environ, "HERE": directory, "RESULT_DIR": directory,
                         "NETHERMIND_OBSERVE_SCRIPT": "observe", "EEZ_PARTITION_SCRIPT": "scope",
                         "EEZ_SUITE": "ingress", "SCENARIO_EXIT": str(status)})
                self.assertEqual(result.returncode, status, result.stderr)
                self.assertEqual(result.stdout.count("upstream-cleanup:" + str(status)), 1)
                self.assertNotIn("unexpected-teardown", result.stdout)
                self.assertEqual("upstream-result:pass" in result.stdout, status == 0)
        teardown = '    bash "$HERE/stop.sh" || true\n'
        for source in (self.driver().replace(teardown, ""), self.driver().replace(teardown, teardown * 2),
                       self.driver().replace(teardown, teardown + "    changed_cleanup_step\n")):
            with self.subTest(source=source), self.assertRaises(ValueError):
                partitions.adapt_driver(source, Path("unused"), "rollup0", "ingress")

    def test_report_requires_original_result_candidate_and_every_observation(self):
        for mutation in (None, "missing-observation", "failed-observation", "failed-result", "wrong-source", "missing-candidate"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                manifest = {"profile": "rollup0", "revision": "a" * 40, "core_revision": "b" * 40}
                plan = self.coverage_plan("rollup0")
                plan["upstream"] = manifest
                result = {"profile": "rollup0", "suite": "wave-mixed", "result": "pass", "exit_code": 0,
                          "commits": {"root": manifest["revision"], "protocol": manifest["core_revision"]}}
                if mutation == "failed-result": result["result"] = "fail"
                if mutation == "wrong-source": result["commits"]["root"] = "c" * 40
                files = {"upstream.json": manifest, "partitions.json": plan, "result.json": result,
                         "scenario-hooks.json": ["verify-eezl2-deployment", "verify-cross-chain-waves"],
                         "verify-eezl2-deployment.json": {"result": "pass"},
                         "verify-cross-chain-waves.json": {"result": "fail" if mutation == "failed-observation" else "pass"},
                         "candidate-prover.json": {"result": "pass"}}
                if mutation == "missing-observation": del files["verify-cross-chain-waves.json"]
                if mutation == "missing-candidate": del files["candidate-prover.json"]
                for name, value in files.items(): (root / name).write_text(json.dumps(value))
                with patch.dict(os.environ, {"EEZ_PROFILE": "rollup0", "EEZ_SUITE": "wave-mixed", "GITHUB_SHA": "d" * 40}):
                    if mutation is None:
                        partitions.report(root)
                        self.assertEqual("pass", json.loads((root / "partition.json").read_text())["result"])
                    else:
                        with self.assertRaises((AssertionError, ValueError, FileNotFoundError)):
                            partitions.report(root)
                        self.assertFalse((root / "partition.json").exists())

    def test_plan_uses_current_supported_targets_and_assigns_each_once(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            driver = root / "testing/kurtosis/scripts/run-protocol-e2e.sh"
            driver.parent.mkdir(parents=True)
            driver.write_text("SUPPORTED_TARGETS=(\n one_way # comment\n multi_tx\n new_target\n)\n")
            result = partitions.plan(root, {"profile": "rollup0"})
            targets = result["suites"]["protocol"]["targets"]
            self.assertCountEqual(["one_way", "multi_tx", "new_target"], targets)
            self.assertEqual(len(targets), len(set(targets)))

    def test_native_plan_keeps_upstream_exclusions_and_new_scenarios(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            names = ["bridge", "bridgeL2", "case2", "case3", "case4", "excluded"]
            for name in names:
                path = root / "eez-core-protocol/script/e2e/scenarios/group" / name / "E2E.s.sol"
                path.parent.mkdir(parents=True)
                path.write_text("// E2E_EXCLUDE_FROM_ALL: local only\n" if name == "excluded" else "original assertion")
            result = partitions.plan(root, {"profile": "rollupx"})
            self.assertCountEqual(names[:-1], result["protocol_cases"])
            self.assertEqual(result["protocol_cases"], result["suites"]["protocol"]["targets"])

    def test_scope_cannot_claim_unexecuted_full_suite(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            value = {"result": "pass", "exit_code": 0, "commits": {}, "images": {}, "proof_flow": {},
                     "state_chaining": "pass", "modes": ["inbound", "outbound"], "safe_head_convergence": "pass"}
            (root / "result.json").write_text(json.dumps(value))
            with patch.dict(os.environ, {"EEZ_PROFILE": "rollupx", "EEZ_SUITE": "wave-mixed", "GITHUB_SHA": "a" * 40}):
                partitions.scope(root)
            result = json.loads((root / "result.json").read_text())
            self.assertFalse(result["suite_complete"])
            self.assertNotIn("state_chaining", result)
            self.assertNotIn("modes", result)

    def test_wave_partitions_keep_original_command_arguments(self):
        original = '''#!/usr/bin/env bash
for mode in inbound outbound mixed; do
    run_check "cross-chain-wave-$mode" env EEZ_WAVE_MODE="$mode" EEZ_WAVE_COUNT=1 EEZ_INCLUDE_REVERTS=1 bash "$HERE/cross-chain-wave.sh"
done

run_check "cross-chain-wave-mixed-pure" env EEZ_WAVE_MODE=mixed-pure EEZ_WAVE_COUNT="${EEZ_MIXED_PURE_WAVE_COUNT:-3}" bash "$HERE/cross-chain-wave.sh"

run_check "state-chaining" bash "$HERE/verify-state-chaining.sh"
'''
        for suite, selected in partitions.WAVE_GROUPS.items():
            with self.subTest(selected=selected), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "waves.sh"
                path.write_text(original)
                partitions.adapt_waves(path, selected)
                adapted = path.read_text()
                self.assertIn("EEZ_INCLUDE_REVERTS=1", adapted)
                self.assertIn("EEZ_WAVE_COUNT=1", adapted)
                modes = "inbound outbound" if suite == "recovery" else "inbound mixed" if suite == "wave-mixed" else "inbound"
                self.assertIn("for mode in " + modes + "; do", adapted)
                if suite == "wave-mixed-pure":
                    self.assertIn("${EEZ_MIXED_PURE_WAVE_COUNT:-3}", adapted)
                    self.assertLess(adapted.index("for mode in inbound; do"), adapted.index('run_check "cross-chain-wave-mixed-pure"'))
                elif suite == "wave-state-chaining":
                    self.assertIn('bash "$HERE/verify-state-chaining.sh"', adapted)
                    self.assertLess(adapted.index("for mode in inbound; do"), adapted.index('run_check "state-chaining"'))

    def test_aggregate_rejects_missing_duplicate_failed_or_changed_partitions(self):
        revision = "a" * 40
        for failure in (None, "missing", "duplicate", "failed", "candidate", "source", "plan", "missing-target", "duplicate-target", "missing-inbound"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                records = []
                for profile in partitions.PROFILES:
                    plan = self.coverage_plan(profile)
                    if failure == "missing-target": plan["suites"]["protocol"]["targets"] = ["bridge"]
                    if failure == "duplicate-target": plan["suites"]["protocol"]["targets"] = ["bridge", "bridge"]
                    if failure == "missing-inbound": plan["suites"]["wave-mixed"]["waves"] = ["mixed"]
                    for suite in partitions.suites(profile):
                        records.append({"result": "pass", "profile": profile, "suite": suite,
                                        "candidate_revision": revision, "upstream": plan["upstream"], "plan": copy.deepcopy(plan)})
                if failure == "missing": records.pop()
                if failure == "duplicate": records.append(copy.deepcopy(records[0]))
                if failure == "failed": records[0]["result"] = "fail"
                if failure == "candidate": records[0]["candidate_revision"] = "b" * 40
                if failure == "source": records[0]["upstream"] = {"profile": "other"}
                if failure == "plan": records[0]["plan"]["unexpected"] = True
                for index, record in enumerate(records):
                    path = root / str(index)
                    path.mkdir()
                    (path / "partition.json").write_text(json.dumps(record))
                with patch.dict(os.environ, {"GITHUB_SHA": revision}):
                    if failure is None:
                        partitions.aggregate(root)
                        self.assertEqual("pass", json.loads((root / "summary.json").read_text())["result"])
                    else:
                        with self.assertRaises((ValueError, AssertionError)):
                            partitions.aggregate(root)
                        self.assertFalse((root / "summary.json").exists())

    def test_full_native_protocol_cannot_pass_with_missing_or_incomplete_evidence(self):
        for failure in (None, "targets", "passed", "duplicate", "suite", "causal"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                manifest = {"profile": "rollupx", "revision": "a" * 40, "core_revision": "b" * 40}
                plan = self.coverage_plan("rollupx")
                plan["upstream"] = manifest
                protocol = {"protocol_commit": manifest["core_revision"], "targets": ["bridge", "bridgeL2"],
                    "passed": ["bridge", "bridgeL2"], "failed": [], "suite": {"complete": True},
                    "evidence_complete": True, "accounting": {"valid": True},
                    "causal_coverage": {"complete": True}, "semantic_blob_coverage": {"complete": True}}
                if failure == "targets": protocol["targets"] = ["bridge"]
                if failure == "passed": protocol["passed"] = ["bridge"]
                if failure == "duplicate": protocol["passed"].append("bridge")
                if failure == "suite": protocol["suite"]["complete"] = False
                if failure == "causal": protocol["causal_coverage"]["complete"] = False
                files = {"upstream.json": manifest, "partitions.json": plan,
                    "result.json": {"result": "pass", "exit_code": 0, "profile": "rollupx", "suite": "protocol",
                        "commits": {"root": manifest["revision"], "protocol": manifest["core_revision"]}},
                    "scenario-hooks.json": ["verify-eezl2-deployment", "run-protocol-e2e"],
                    "verify-eezl2-deployment.json": {"result": "pass"},
                    "run-protocol-e2e.json": {"result": "pass"}, "candidate-follower.json": {"result": "pass"},
                    "protocol-e2e/summary.json": protocol}
                for name, value in files.items():
                    target = root / name
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_text(json.dumps(value))
                with patch.dict(os.environ, {"EEZ_PROFILE": "rollupx", "EEZ_SUITE": "protocol", "GITHUB_SHA": "d" * 40}):
                    if failure is None:
                        partitions.report(root)
                        self.assertEqual("pass", json.loads((root / "partition.json").read_text())["result"])
                    else:
                        with self.assertRaises((ValueError, AssertionError)):
                            partitions.report(root)
                        self.assertFalse((root / "partition.json").exists())

    @staticmethod
    def coverage_plan(profile):
        result = {"upstream": {"profile": profile}, "protocol_cases": ["bridge", "bridgeL2"],
                  "suites": {suite: {} for suite in partitions.suites(profile)}}
        result["suites"]["protocol"]["targets"] = ["bridge", "bridgeL2"]
        for suite, waves in partitions.WAVE_GROUPS.items():
            result["suites"][suite]["waves"] = list(waves)
        return result

    @staticmethod
    def driver():
        driver = '''set -euo pipefail
bash() {
    if [[ "$1" == */stop.sh ]]; then echo unexpected-teardown; return 0; fi
    if [[ "$1" == */verify-ingress-admission.sh ]]; then return "${SCENARIO_EXIT:-0}"; fi
}
python3() { :; }
write_result() { echo "upstream-result:$1"; }
cleanup() {
    local status=$?
    trap - EXIT
    echo "upstream-cleanup:$status"
    bash "$HERE/stop.sh" || true
    exit "$status"
}
trap cleanup EXIT
'''
        for name in ("verify-eezl2-deployment", "verify-cross-chain-waves", "run-protocol-e2e",
                     "verify-ingress-admission", "verify-fault-recovery"):
            driver += 'bash "$HERE/scripts/' + name + '.sh"\nbash "$NETHERMIND_OBSERVE_SCRIPT" ' + name + '\n'
        driver += 'bash "$NETHERMIND_OBSERVE_SCRIPT" recovery\nwrite_result pass 0\n'
        return driver + 'if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then\n echo misleading-full-pass\nfi\n'
