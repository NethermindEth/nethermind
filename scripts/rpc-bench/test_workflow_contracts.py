#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import corpus_results  # noqa: E402


REPO = Path(__file__).parents[2]
WORKFLOW = REPO / ".github" / "workflows" / "run-rpc-benchmarks.yml"
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


def run_block(name):
    """The script a step's `run: |` block hands to bash, de-indented."""
    lines = RpcBenchmarkWorkflowTests.step(name).splitlines()
    run_line = next(index for index, line in enumerate(lines) if line == "        run: |")
    body = []
    for line in lines[run_line + 1:]:
        if line and len(line) - len(line.lstrip(" ")) < 10:
            break
        body.append(line[10:] if line else "")
    return "\n".join(body) + "\n"


def expand(script, values):
    """Fills ${{ }} expressions in the way Actions does: into the script's text, before bash parses it."""
    return re.sub(r"\$\{\{\s*(.*?)\s*\}\}", lambda match: values.get(match.group(1), "x"), script)


def run_in_repo(script_text, environment, *bash_args, timeout=60):
    """Runs a step's script from the repository root, with the step's environment exported first."""
    script = REPO / f".rpc-workflow-test-{os.getpid()}.sh"
    wrapper = REPO / f".rpc-workflow-test-{os.getpid()}.wrapper.sh"
    script.write_bytes(script_text.encode("utf-8"))
    wrapper_lines = ["#!/usr/bin/env bash", "set -e"]
    wrapper_lines.extend(f"export {name}={shlex.quote(value)}" for name, value in environment.items())
    wrapper_lines.append(" ".join(["exec bash", *bash_args, shlex.quote(script.name)]))
    wrapper.write_bytes(("\n".join(wrapper_lines) + "\n").encode("utf-8"))
    try:
        return subprocess.run([BASH, "--noprofile", "--norc", wrapper.name], cwd=REPO, env=os.environ.copy(),
                              capture_output=True, text=True, timeout=timeout)
    finally:
        wrapper.unlink(missing_ok=True)
        script.unlink(missing_ok=True)


def github_outputs(text):
    """A $GITHUB_OUTPUT file's values: `name=value` lines and `name<<DELIMITER` blocks."""
    outputs, lines, index = {}, text.splitlines(), 0
    while index < len(lines):
        line, index = lines[index], index + 1
        block = re.fullmatch(r"([A-Za-z_][A-Za-z0-9_]*)<<(\S+)", line)
        if block:
            end = lines.index(block.group(2), index)
            outputs[block.group(1)], index = "\n".join(lines[index:end]), end + 1
        elif "=" in line:
            name, value = line.split("=", 1)
            outputs[name] = value
    return outputs


def dispatch_defaults():
    """The resolver's IN_* environment for a workflow_dispatch that leaves every input at its default."""
    workflow = WORKFLOW.read_text(encoding="utf-8")
    inputs = workflow[workflow.index("    inputs:\n"):workflow.index("\n  pull_request:")]
    defaults, name = {}, None
    for line in inputs.splitlines():
        if re.fullmatch(r"      [a-z_]+:", line):
            name = line.strip().rstrip(":")
        elif line.startswith("        default: "):
            defaults[name] = line[len("        default: "):].strip('"')
    wiring = re.findall(r"^          (IN_[A-Z_]+): \$\{\{ inputs\.([a-z_]+) \}\}$",
                        RpcBenchmarkWorkflowTests.step("Resolve configuration"), re.M)
    return {variable: defaults.get(input_name, "") for variable, input_name in wiring}

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

        # A baseline measured as eth_call is not a baseline for a debug_traceCall or trace_call run.
        for knob in ("corpus_method", "trace_call_tracer", "trace_call_types"):
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
        # scripts/ci/test_rpc_runner_workspace.py drives the gate against each filesystem.
        self.assertIn("{{.DockerRootDir}}", reclaim_step)
        self.assertIn('have_space "${MIN_FREE_GB}" "${image_roots[@]}"', reclaim_step)
        # `/` still has to hold the runner itself.
        self.assertIn("have_space 1 /", reclaim_step)

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

    def test_jsonbench_sweep_requires_baseline_when_clients_are_not_overridden(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        block = workflow[workflow.index("            jsonbench-sweep)"):workflow.index("            jsonbench) preset_cfg=")]
        # The cache sentinel is valid when tool_config supplies both sweep arms, but cannot be interpolated
        # into a derived image when clients are absent.
        self.assertIn('if [[ "${user_clients_type}" == "absent" ]]; then', block)
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


@unittest.skipUnless(BASH, "no usable bash to execute the workflow resolver")
class ResolverExecutionTests(unittest.TestCase):
    @staticmethod
    def resolver_script():
        return run_block("Resolve configuration")

    def run_resolver(self, tool_config: str, baseline_image: str = "cache", **inputs):
        output = REPO / f".rpc-resolver-test-{os.getpid()}"
        output.touch()
        environment = {
            "EVENT_NAME": "workflow_dispatch",
            "PR_HEAD_BRANCH": "",
            "PUSH_BRANCH": "feature/rpc-resolver-test",
            "IN_TOOL": "jsonbench-sweep",
            "IN_ARCH": "amd64",
            "IN_DOCKER_IMAGE": "",
            "IN_BASELINE_IMAGE": baseline_image,
            "IN_RPS": "1",
            "IN_REQUESTS": "1",
            "IN_ROUNDS": "1",
            "IN_TIMINGS_PASSES": "0",
            "IN_WARMUP": "0",
            "IN_CORPUS": "eth-call-corpus-test.jsonl.gz",
            "IN_DURATION": "1",
            "IN_SNAPSHOT_BLOCK": "",
            "IN_DOTTRACE": "false",
            "IN_CLIENT": "nethermind",
            "IN_REFERENCE_CLIENT": "none",
            "IN_PERF": "false",
            "IN_DOTNET_TRACE": "false",
            "IN_STATE_LAYOUT": "flat",
            "IN_FLAGS": "",
            "IN_TOOL_CONFIG": tool_config,
            "IN_NODE_CONFIG": "{}",
            "WR_EVENT": "",
            "WR_CONCLUSION": "",
            "WR_HEAD_BRANCH": "",
            "WR_HEAD_SHA": "",
            "GITHUB_OUTPUT": output.name,
        }
        environment.update(inputs)
        environment["IN_TOOL_CONFIG"] = tool_config
        try:
            return run_in_repo(self.resolver_script(), environment), output.read_text(encoding="utf-8")
        finally:
            output.unlink(missing_ok=True)

    @staticmethod
    def run_sweep_step(outputs):
        """The `Run RPC sweep` step on the resolver's outputs, as far as it gets without a benchmark box."""
        step = RpcBenchmarkWorkflowTests.step("Run RPC sweep")
        environment = {variable: outputs.get(output, "") for variable, output in
                       re.findall(r"^          ([A-Z_]+): \$\{\{ needs\.resolve\.outputs\.([a-z_]+) \}\}$", step, re.M)}
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch).as_posix()
            # Nothing here is the runner's: the snapshot set is absent, so the sweep stops at the first thing it
            # needs from the box - after every check of its configuration.
            environment.update({"SCRATCH_ROOT": f"{root}/scratch", "SNAPSHOT_ROOT": f"{root}/snapshots",
                                "CORPUS_DIR": f"{root}/corpus", "OUT_DIR": f"{root}/out", "STATE_DIR": f"{root}/state",
                                "BASELINE_CACHE_HIT": "true", "BASELINE_FALLBACK_IMAGE": "nethermindeth/nethermind:master"})
            return run_in_repo(expand(run_block("Run RPC sweep"), {}), environment, "-eo pipefail", timeout=120)

    def test_jsonbench_sweep_accepts_complete_user_clients_with_cache_sentinel(self):
        clients = "nethermind@baseline/image:tag nethermind@candidate/image:tag"
        result, output = self.run_resolver(json.dumps({"clients": clients}))

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        tool_config = re.search(r"tool_config<<TOOLCFG\n(.*?)\nTOOLCFG", output, re.S)
        self.assertIsNotNone(tool_config)
        self.assertEqual(clients, json.loads(tool_config.group(1))["clients"])

    def test_jsonbench_sweep_requires_real_or_well_formed_clients(self):
        for config, expected in (("{}", "explicit baseline_image or tool_config.clients"),
                                 (json.dumps({"clients": []}), "clients must be a string"),
                                 (json.dumps({"clients": "   "}), "clients must be a non-empty string")):
            with self.subTest(config=config):
                result, _ = self.run_resolver(config)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(expected, result.stdout + result.stderr)

    def test_an_explicit_corpus_passes_replaces_the_derived_request_count(self):
        # The documented passes-based sizing on an otherwise default dispatch: the inputs derive corpus_requests too,
        # and the sweep refuses a config carrying both.
        result, output = self.run_resolver(json.dumps({"corpus_passes": 40}), **dispatch_defaults())
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        outputs = github_outputs(output)
        tool_config = json.loads(outputs["tool_config"])
        self.assertEqual(40, tool_config["corpus_passes"])
        self.assertNotIn("corpus_requests", tool_config)

        sweep = self.run_sweep_step(outputs)
        self.assertNotIn("mutually exclusive", sweep.stdout + sweep.stderr)
        self.assertIn("no nethermind snapshot set", sweep.stdout + sweep.stderr)

    def test_a_tool_config_that_sets_both_sizings_is_still_refused(self):
        config = json.dumps({"corpus_passes": 40, "corpus_requests": 5000})
        result, output = self.run_resolver(config, **dispatch_defaults())
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

        sweep = self.run_sweep_step(github_outputs(output))
        self.assertNotEqual(0, sweep.returncode)
        self.assertIn("corpus_requests and corpus_passes are mutually exclusive", sweep.stdout + sweep.stderr)


@unittest.skipUnless(BASH, "no usable bash to execute the workflow step")
class PrintConfigurationTests(unittest.TestCase):
    def test_tool_config_is_logged_as_data(self):
        script = run_block("Print resolved configuration")
        marker = REPO / f".rpc-print-test-{os.getpid()}.executed"
        self.addCleanup(marker.unlink, missing_ok=True)
        for value in ({"extra_args": '--filter "(eth_call|eth_getLogs)"'},
                      {"label": "it's (quoted)"},
                      {"extra_args": f"$(touch {marker.name})"},
                      {"extra_args": f"`touch {marker.name}`"}):
            tool_config = json.dumps(value, separators=(",", ":"))
            with self.subTest(tool_config=tool_config):
                result = run_in_repo(expand(script, {"needs.resolve.outputs.tool_config": tool_config}),
                                     {"TOOL_CONFIG": tool_config}, "-e")
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn(f"tool_config: {tool_config}\n", result.stdout)
                self.assertFalse(marker.exists(), "a command substitution inside tool_config was executed")


@unittest.skipUnless(BASH, "no usable bash to run cpu-stabilize.sh")
class CpuStabilizeExecutionTests(unittest.TestCase):
    """cpu-stabilize.sh against a fake sysfs tree in which two CPUs share a cpufreq policy, as a cluster's do."""

    ORIGINALS = {"policy0": ("schedutil", "3700000"), "policy2": ("powersave", "3500000")}
    CPUS = {"cpu0": "policy0", "cpu1": "policy0", "cpu2": "policy2"}

    def setUp(self):
        scratch = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, scratch, ignore_errors=True)
        self.sysfs, self.saved, shim = scratch / "cpu", scratch / "state" / "cpu-sysfs.orig", scratch / "bin"
        for policy, (governor, max_freq) in self.ORIGINALS.items():
            (self.sysfs / "cpufreq" / policy).mkdir(parents=True)
            (self.sysfs / "cpufreq" / policy / "scaling_governor").write_bytes(f"{governor}\n".encode())
            (self.sysfs / "cpufreq" / policy / "scaling_max_freq").write_bytes(f"{max_freq}\n".encode())
        (self.sysfs / "cpufreq" / "boost").write_bytes(b"1\n")
        for cpu, policy in self.CPUS.items():
            (self.sysfs / cpu).mkdir()
            try:
                os.symlink(self.sysfs / "cpufreq" / policy, self.sysfs / cpu / "cpufreq", target_is_directory=True)
            except OSError:
                self.skipTest("this box cannot create the symlinks a shared policy needs")
        # write_sys escalates through sudo when not root; this tree is the test's own.
        shim.mkdir()
        (shim / "sudo").write_bytes(b'#!/bin/sh\nexec "$@"\n')
        (shim / "sudo").chmod(0o755)
        self.environment = {**os.environ, "PATH": f"{shim}{os.pathsep}{os.environ['PATH']}",
                            "CPU_SYSFS": self.sysfs.as_posix(), "STATE_DIR": self.saved.parent.as_posix(),
                            "CPU_MAX_FREQ_KHZ": "3000000"}

    def stabilize(self, action):
        result = subprocess.run([BASH, "--noprofile", "--norc", CPU_STABILIZE.as_posix(), action],
                                env=self.environment, capture_output=True, text=True, timeout=60)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def setting(self, cpu, name):
        return (self.sysfs / cpu / "cpufreq" / name).read_text().strip()

    def test_cpus_sharing_a_policy_get_their_governor_and_frequency_back(self):
        self.stabilize("apply")
        for cpu in self.CPUS:
            self.assertEqual("performance", self.setting(cpu, "scaling_governor"), cpu)
            self.assertEqual("3000000", self.setting(cpu, "scaling_max_freq"), cpu)
        self.assertEqual("0", (self.sysfs / "cpufreq" / "boost").read_text().strip())
        # One record per setting, every one taken before the first write: none is a value apply itself wrote.
        records = [line.split("\t") for line in self.saved.read_text().splitlines()]
        self.assertEqual(1 + 2 * len(self.ORIGINALS), len(records))
        self.assertEqual(len(records), len({path for path, _ in records}))
        self.assertFalse({value for _, value in records} & {"performance", "3000000", "0"}, records)

        self.stabilize("restore")
        for cpu, policy in self.CPUS.items():
            governor, max_freq = self.ORIGINALS[policy]
            self.assertEqual(governor, self.setting(cpu, "scaling_governor"), cpu)
            self.assertEqual(max_freq, self.setting(cpu, "scaling_max_freq"), cpu)
        self.assertEqual("1", (self.sysfs / "cpufreq" / "boost").read_text().strip())
        self.assertFalse(self.saved.exists())


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
