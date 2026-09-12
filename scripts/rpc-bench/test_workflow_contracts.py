#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import json
import re
import shutil
import subprocess
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import corpus_results  # noqa: E402


WORKFLOW = Path(__file__).parents[2] / ".github" / "workflows" / "run-rpc-benchmarks.yml"
LIB = Path(__file__).parent / "lib.sh"
SWEEP = Path(__file__).parent / "run-rpc-sweep.sh"
JSONBENCH = Path(__file__).parent / "run-jsonbench.sh"
CPU_STABILIZE = Path(__file__).parent / "cpu-stabilize.sh"


def _usable_bash():
    """The shell helpers are exercised for real; a box without a working bash skips those tests."""
    bash = shutil.which("bash")
    try:
        return bash if bash and subprocess.run([bash, "-c", "echo ok"], capture_output=True, text=True,
                                               timeout=60).stdout.strip() == "ok" else None
    except OSError:
        return None


BASH = _usable_bash()

# tool_config keys the sweep reads that do not shape what the cell measures, so they stay out of the cell
# fingerprint: the arms themselves and how they are compared, repeats of the same cell, the json-bench-only
# workload keys, reporting/gate knobs, and the parity-diff switches.
NON_SHAPING_KEYS = {
    "clients", "corpus_baseline", "rounds",
    "iso_configs", "iso_duration",
    "db_isolation_allow_snapshot_mutation", "resource_sampling", "parity_diffs",
    "max_divergence_indexes", "max_fail_rate_pct",
}


class RpcBenchmarkWorkflowTests(unittest.TestCase):
    @staticmethod
    def step(name):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        start = workflow.index(f"      - name: {name}")
        end = workflow.index("\n      - name:", start + 1)
        return workflow[start:end]

    @classmethod
    def cell_keys(cls):
        resolve = cls.step("Resolve configuration")
        return set(json.loads(re.search(r"cell_keys='(\[.*?\])'", resolve, re.S).group(1)))

    def test_cache_keys_carry_the_cell_fingerprint(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")

        # Aggregates measured on a differently shaped cell are not a baseline for this run: the fingerprint is part
        # of the key on both sides, so a mismatch misses and the run measures master in-job instead.
        prefix = ("rpc-corpus-baseline-${{ needs.resolve.outputs.arch }}-${{ needs.resolve.outputs.corpus_key }}"
                  "-${{ needs.resolve.outputs.cell_key }}-")
        self.assertIn(f"restore-keys: {prefix}", workflow)
        self.assertIn(f"key: {prefix}lookup-${{{{ github.run_id }}}}", workflow)
        self.assertIn(f"key: {prefix}${{{{ github.run_id }}}}", workflow)

    def test_every_cell_shaping_sweep_knob_is_fingerprinted(self):
        exported = {key for key, _ in re.findall(r"\b([a-z][a-z_0-9]*):([A-Z][A-Z_0-9]*)\b", self.step("Run RPC sweep"))}
        self.assertIn("corpus_requests", exported)
        self.assertIn("node_env_vars", exported)

        # rps_list is exported separately (an absent list means the default, an empty one means no k6 cells).
        self.assertEqual(exported - NON_SHAPING_KEYS, self.cell_keys() - {"rps_list"})

    def test_the_node_envelope_is_fingerprinted_too(self):
        resolve = self.step("Resolve configuration")

        # A cell measured under a different CPU cap or container envelope is not comparable either.
        shape = re.search(r"cell_shape=\"\$\(jq.*?\)\"", resolve, re.S).group(0)
        for knob in ("cpu_max_freq_khz", "cpuset", "memory"):
            self.assertIn(knob, shape)
        self.assertIn('cell_key="$(printf', resolve)

    def test_the_recorded_baseline_names_the_cell_it_was_measured_on(self):
        assemble = self.step("Assemble this run as the master baseline")

        self.assertIn("cell_key: $cell_key", assemble)
        self.assertIn("cell: $cell", assemble)

    def test_arm_reclaim_keep_list_derives_refs_the_way_the_sweep_does(self):
        reclaim_step = self.step("Reclaim root disk before pulling")

        # An open-coded copy of the sweep's split drifted from it once already; both sides call the same helper now,
        # so SweepShellHelperTests can check the behaviour instead of the spelling of a sed program.
        self.assertIn("source scripts/rpc-bench/lib.sh", reclaim_step)
        self.assertIn('sweep_keep+="$(arm_image "${entry}")"', reclaim_step)
        self.assertNotIn("s/.*@//", reclaim_step)

    def test_arm_reclaim_headroom_follows_the_docker_image_store(self):
        reclaim_step = self.step("Reclaim root disk before pulling")

        # A runner may keep its images off `/`; charging the pull headroom to `/` then blocks a box that has room.
        self.assertIn("{{.DockerRootDir}}", reclaim_step)
        self.assertIn("containerd config dump", reclaim_step)
        self.assertIn('avail_gb "${image_store}"', reclaim_step)
        self.assertNotIn("MIN_FREE_GB", reclaim_step)
        # `/` still has to hold RUNNER_TEMP: k6 fixtures, corpus results and logs.
        self.assertIn("MIN_ROOT_FREE_GB", reclaim_step)
        self.assertIn("avail_gb /", reclaim_step)

    def test_a_cached_baseline_from_another_schema_or_cell_is_dropped_before_the_sweep(self):
        check = self.step("Validate the cached master baseline")
        workflow = WORKFLOW.read_text(encoding="utf-8")

        # `Render corpus comparison` is best-effort, so an unrenderable cached tree would surface as silence.
        # It has to be rejected before the sweep chooses whether to measure master in this job.
        self.assertIn(f'CORPUS_BASELINE_SCHEMA: "{corpus_results.BASELINE_SCHEMA}"', workflow)
        self.assertIn(".schema // empty", check)
        self.assertIn(".cell_key // empty", check)
        self.assertIn('[[ "${usable}" == "true" ]] || rm -rf "${BASELINE_DIR}"', check)
        self.assertIn("usable=${usable}", check)
        self.assertLess(workflow.index("- name: Restore the cached master baseline"),
                        workflow.index("- name: Validate the cached master baseline"))
        self.assertLess(workflow.index("- name: Validate the cached master baseline"),
                        workflow.index("- name: Run RPC sweep"))
        # Both consumers of the cache take the verdict, not the bare cache hit.
        self.assertIn("BASELINE_CACHE_HIT: ${{ steps.baseline-check.outputs.usable == 'true' }}", workflow)
        self.assertIn("steps.baseline-check.outputs.usable == 'true' && steps.stage-corpus-results.outcome == 'success'", workflow)
        self.assertNotIn("steps.baseline-cache.outputs.cache-matched-key != ''", workflow)

    def test_the_master_baseline_group_lets_the_running_refresh_finish(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")

        # Cancelling mid-run would starve the refresh: a cell is tens of minutes downstream of the image build.
        self.assertIn("cancel-in-progress: false", workflow)
        self.assertNotIn("cancel-in-progress: true", workflow)


@unittest.skipUnless(BASH, "no usable bash to run the shell helpers")
class SweepShellHelperTests(unittest.TestCase):
    """lib.sh helpers the sweep and the workflow both depend on, run for real."""

    def arm_image(self, entry):
        result = subprocess.run([BASH, "-c", f'source "{LIB.as_posix()}"; arm_image "$1"', "bash", entry],
                                capture_output=True, text=True, check=True)
        return result.stdout.strip()

    def test_arm_image_reproduces_the_sweeps_own_split(self):
        self.assertEqual(self.arm_image("nethermind@repo/nm:pr"), "repo/nm:pr")
        # No image: the sweep falls back to NM_IMAGE, which the reclaim keep-list already holds.
        self.assertEqual(self.arm_image("nethermind"), "")
        self.assertEqual(self.arm_image("nethermind#NETHERMIND_FOO=one"), "")
        # Per-arm options are stripped before the split, so an '@' in an option value keeps the image ...
        self.assertEqual(self.arm_image("nethermind@repo/nm:pr#NETHERMIND_FOO=a@b"), "repo/nm:pr")
        # ... and the split is on the first '@', so a digest ref survives whole.
        self.assertEqual(self.arm_image("nethermind@repo/nm@sha256:abc"), "repo/nm@sha256:abc")

    def test_the_sweep_and_the_workflow_use_the_helper(self):
        self.assertIn('img="$(arm_image "$entry")"', SWEEP.read_text(encoding="utf-8"))
        self.assertIn("arm_image", LIB.read_text(encoding="utf-8"))

class BenaadamsFindingsTests(unittest.TestCase):
    """Four findings from @benaadams' review: each turned a documented input into a silently wrong run."""

    def test_jsonbench_sweep_refuses_the_corpus_only_cache_sentinel(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        block = workflow[workflow.index("            jsonbench-sweep)"):workflow.index("            jsonbench) preset_cfg=")]
        # baseline_image defaults to 'cache', a sentinel resolved only for the corpus presets. Interpolated
        # here it became an image name and the arm died ~90s into a 30-minute booking.
        self.assertIn('[[ "${baseline_image}" != "cache" ]] || fail', block)

    def test_the_snapshot_block_input_reaches_both_sweep_presets(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        # It is in cell_keys, so a fingerprint that could never see it made the cache look safer than it was.
        self.assertIn('"snapshot_block"', workflow)
        for preset in ("            corpus-ab|corpus-baseline)", "            jsonbench-sweep)"):
            block = workflow[workflow.index(preset):]
            block = block[:block.index(';;')]
            self.assertIn('--arg snapshot "${snapshot_block}"', block, preset)
            self.assertIn('{snapshot_block: $snapshot}', block, preset)

    def test_a_sweep_arm_that_never_starts_fails_the_run(self):
        sweep = SWEEP.read_text(encoding="utf-8")
        # A sweep exists to compare arms, so reporting success on half a matrix is worse than failing.
        self.assertIn("arm_fail=$((arm_fail + 1))", sweep)
        gate = next(line for line in sweep.splitlines() if line.startswith('[[ "$arm_fail" -eq 0 ]]'))
        self.assertIn("::error::", gate)
        self.assertIn("fail=1", gate)

    def test_the_resource_sampler_teardown_cannot_fail_a_finished_benchmark(self):
        jsonbench = JSONBENCH.read_text(encoding="utf-8")
        teardown = jsonbench[jsonbench.index("stop_resource_sampler() {"):]
        teardown = teardown[:teardown.index(chr(10) + "}")]
        # The sampler exits early on an unknown cgroup root and bash reaps it, so under errexit an unguarded
        # kill or wait aborted the script after the benchmark had already succeeded.
        self.assertIn('kill -TERM "$sampler_pid" 2>/dev/null || true', teardown)
        self.assertIn('wait "$sampler_pid" 2>/dev/null || true', teardown)

    def test_cpu_stabilize_keeps_the_originals_unless_every_one_is_restored(self):
        script = CPU_STABILIZE.read_text(encoding="utf-8")
        restore = script[script.index("restore() {"):script.index('case "${1:-}" in')]
        # write_sys swallows its errors, so an unconditional rm left the box capped with no original to
        # return to - and the next apply would record the cap as the original.
        self.assertIn('if [[ "$n" -eq "$total" ]]; then', restore)
        self.assertIn('rm -f "$SAVED"', restore)
        self.assertIn("could not be restored", restore)
        self.assertLess(restore.index('total=$((total + 1))'), restore.index('write_sys "$path" "$value"'))
        # counted after the skip guard, or a blank line would block cleanup forever
        self.assertLess(restore.index('|| continue'), restore.index('total=$((total + 1))'))


class SweepContractTests(unittest.TestCase):
    """Contracts read out of the sweep's text; no shell needed, so these must never skip."""

    def test_an_unchecked_saved_baseline_fails_the_run(self):
        sweep = SWEEP.read_text(encoding="utf-8")

        # With a saved baseline the run has one arm, so parity is its only correctness gate: skipping it silently
        # let a PR merge with a green job and the check never executed.
        gate = next(line for line in sweep.splitlines() if line.startswith('[[ "$parity_skipped" -eq 0 ]]'))
        self.assertIn("::error::", gate)
        self.assertIn("fail=1", gate)
        skip_branch = sweep[sweep.index("elif (( status == 2 ))"):sweep.index("parity_skipped=$((parity_skipped + 1))")]
        self.assertIn("::error::", skip_branch)
        # A 'use' run whose saved baseline is absent never reaches compare at all, so it must trip the same gate
        # rather than capture this arm as its own baseline and pass.
        self.assertIn("no saved parity baseline for corpus", sweep)
        missing_branch = sweep[sweep.index('if [[ "$CORPUS_BASELINE" == "use" ]]; then'):]
        self.assertIn("parity_skipped=$((parity_skipped + 1))",
                      missing_branch[:missing_branch.index("-- PARITY")])
        # Exit 2 also covers an unreachable node and an unreadable corpus, which no re-recording fixes.
        self.assertNotIn("rerun the master baseline", sweep)

    def test_the_saved_parity_baseline_is_renamed_into_place(self):
        sweep = SWEEP.read_text(encoding="utf-8")

        # A cancelled run must not leave a truncated state at the final path: the read side accepts any non-empty
        # file and would then report "parity not checked" on every later run.
        self.assertIn('cp "$PARITY_STATE/$clabel.json" "$saved.tmp"', sweep)
        self.assertIn('mv -f "$saved.tmp" "$saved"', sweep)
        self.assertNotIn('cp "$PARITY_STATE/$clabel.json" "$saved"', sweep)
        self.assertIn('> "$CORPUS_BASELINE_DIR/$clabel.label.tmp"', sweep)
        self.assertIn('mv -f "$CORPUS_BASELINE_DIR/$clabel.label.tmp" "$CORPUS_BASELINE_DIR/$clabel.label"', sweep)
        # State before label: reversed, an interruption between the two renames leaves the new master's label
        # naming the previous master's responses, and every later run then compares against the wrong set.
        self.assertLess(sweep.index('mv -f "$saved.tmp" "$saved"'),
                        sweep.index('mv -f "$CORPUS_BASELINE_DIR/$clabel.label.tmp"'))
        # A literal newline inside a single-quoted format reads like the line-continuation damage fixed once before.
        self.assertNotIn("printf '%s\n", sweep)


if __name__ == "__main__":
    unittest.main()
