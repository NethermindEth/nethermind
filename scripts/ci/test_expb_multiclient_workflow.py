#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Exercise the client policy and image list in the EXPB workflow.

The tests execute the checked-in Bash bodies so resolver and image-list cases cover the same
validation and output construction used by GitHub Actions. The multi-image campaign itself
(rendering, gates and summary) is covered by scripts/expb/test_sequential_driver.py.

Run with: python -m unittest scripts.ci.test_expb_multiclient_workflow
"""

import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
WORKFLOW = REPO / ".github/workflows/run-expb-reproducible-benchmarks.yml"


def extract_steps(path, step_name):
    """Extract dedented ``run: |`` bodies for all steps with the given name."""
    lines = path.read_bytes().decode("utf-8-sig").replace("\r\n", "\n").split("\n")
    bodies = []
    for index, line in enumerate(lines):
        header = re.match(r"^(?P<indent>\s*)- name: (?P<name>.*?)\s*$", line)
        if not header or header["name"] != step_name:
            continue
        run_index = next(
            i
            for i in range(index + 1, len(lines))
            if re.match(r"^\s+run:\s*\|\s*$", lines[i])
        )
        run_indent = len(re.match(r"^(\s+)", lines[run_index]).group(1))
        body_indent = run_indent + 2
        body = []
        for candidate in lines[run_index + 1 :]:
            if candidate and len(candidate) - len(candidate.lstrip(" ")) < body_indent:
                break
            body.append(candidate[body_indent:] if candidate else "")
        bodies.append("\n".join(body) + "\n")
    return bodies


def extract_step(path, step_name):
    """Extract the dedented ``run: |`` body for one named workflow step."""
    bodies = extract_steps(path, step_name)
    if len(bodies) != 1:
        raise AssertionError(
            "expected one workflow step {!r}, found {}".format(step_name, len(bodies))
        )
    return bodies[0]


def find_bash():
    if os.name != "nt":
        return shutil.which("bash")
    git = shutil.which("git")
    if git:
        candidate = Path(git).parent.parent / "bin" / "bash.exe"
        if candidate.exists():
            return str(candidate)
    bash = shutil.which("bash")
    return bash if bash and "System32" not in bash else None


def to_bash(path):
    """Convert a native path to the form accepted by Git Bash."""
    if os.name != "nt":
        return str(path)
    text = str(path).replace("\\", "/")
    return "/" + text[0].lower() + text[2:] if len(text) > 1 and text[1] == ":" else text


def parse_output(path):
    values = {}
    lines = path.read_text(encoding="utf-8").splitlines()
    index = 0
    while index < len(lines):
        line = lines[index]
        if line.endswith("<<EOF"):
            key = line[: -len("<<EOF")]
            index += 1
            multiline = []
            while index < len(lines) and lines[index] != "EOF":
                multiline.append(lines[index])
                index += 1
            values[key] = "\n".join(multiline)
        elif "=" in line:
            key, value = line.split("=", 1)
            values[key] = value
        index += 1
    return values


class ExpbWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bash = find_bash()
        if cls.bash is None:  # pragma: no cover - environment guard
            raise unittest.SkipTest("a POSIX bash is required to execute workflow steps")
        cls.resolver = extract_step(WORKFLOW, "Resolve branch and configuration")
        cls.matrix = extract_step(WORKFLOW, "Resolve Docker images")
        # The single-image job installs expb alone; the sequential campaign job installs yq with it.
        cls.installers = extract_steps(WORKFLOW, "Install or upgrade expb") + extract_steps(
            WORKFLOW, "Install or upgrade expb and yq"
        )
        if len(cls.installers) != 2:
            raise AssertionError("expected both single and multi-image EXPB install steps")
        # Only the single-image job analyzes in Bash; the campaign driver owns the multi-image gates.
        cls.analyzers = extract_steps(WORKFLOW, "Analyze benchmark output")
        if len(cls.analyzers) != 1:
            raise AssertionError("expected exactly the single-image analyze step")
        cls.snapshot_preflight = extract_step(WORKFLOW, "Verify client snapshot and provenance")

    def run_body(self, body, values, output_name="github-output"):
        with tempfile.TemporaryDirectory(prefix="expb-workflow-test-") as temp_dir:
            temp_path = Path(temp_dir)
            output = temp_path / output_name
            output.touch()
            env = dict(os.environ)
            env.update(values)
            env["GITHUB_OUTPUT"] = to_bash(output)
            script = temp_path / "workflow-step.sh"
            script.write_text(body, encoding="utf-8")
            proc = subprocess.run(
                [self.bash, "--noprofile", "--norc", "-eo", "pipefail", to_bash(script)],
                env=env,
                cwd=REPO,
                capture_output=True,
                text=True,
            )
            return proc, parse_output(output), temp_path

    def test_render_replaces_the_cpu_quota_with_whole_core_affinity(self):
        yq = os.environ.get("YQ") or shutil.which("yq")
        if not yq:
            self.skipTest("Mike Farah yq is required (set YQ or add it to PATH)")
        renderers = extract_steps(WORKFLOW, "Render benchmark config")
        self.assertEqual(1, len(renderers))
        for index, renderer in enumerate(renderers):
            with self.subTest(renderer=index), tempfile.TemporaryDirectory() as directory:
                topology = Path(directory) / "cpu"
                for cpu in range(16):
                    (topology / f"cpu{cpu}" / "topology").mkdir(parents=True)
                    (topology / f"cpu{cpu}" / "topology" / "thread_siblings_list").write_text(
                        f"{cpu % 8},{cpu % 8 + 8}\n", encoding="utf-8"
                    )
                source = Path(directory) / "source.yaml"
                rendered = Path(directory) / "rendered.yaml"
                original = (
                    'resources:\n  cpu: 8\n  cpuset: "2-7,10-15"\n'
                    '  infra_cpuset: "0-1,8-9"\n  mem: 64g\n'
                    'scenarios:\n  nethermind:\n    amount: 10\n'
                )
                source.write_text(original, encoding="utf-8")
                start = renderer.index('sed \\')
                end = renderer.index('scenario_key="${SCENARIO_NAME}"')
                proc, _, _ = self.run_body(renderer[start:end], {
                    "YQ": to_bash(yq),
                    "SOURCE_CONFIG_FILE": to_bash(source),
                    "RENDERED_CONFIG_FILE": to_bash(rendered),
                    "DOCKER_TAG": "test",
                    "DELAY_SECONDS": "0",
                    "AMOUNT": "10",
                    "EXPB_DATA_DIR": "/data/expb-data",
                    "FLAT_SNAPSHOT_DIR": "/data/snapshot",
                    "FLAT_SNAPSHOT_BLOCK_DIR": "/data/snapshot-block",
                    "SCENARIO_NAME": "test",
                    "CPU_TOPOLOGY_DIR": to_bash(topology),
                })
                self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
                result = subprocess.run(
                    [yq, "-o=json", ".resources", str(rendered)],
                    capture_output=True, text=True, check=True,
                )
                self.assertEqual({
                    "cpu": 0, "cpuset": "2,3,4,5,10,11,12,13",
                    "infra_cpuset": "0-1,8-9", "mem": "64g",
                }, json.loads(result.stdout))
                self.assertEqual(original, source.read_text(encoding="utf-8"))

    def run_resolver(self, **overrides):
        values = {
            "EVENT_NAME": "workflow_dispatch",
            "PUSH_BRANCH": "feature/test-client",
            "DISPATCH_CLIENT": "nethermind",
            "DISPATCH_MEASUREMENT_SOURCE": "auto",
            "DISPATCH_STATE_LAYOUT": "flat",
            "DISPATCH_ARCH": "amd64",
            "DISPATCH_PAYLOAD_SET": "fusaka",
            "DISPATCH_EXPB_REPO": "NethermindEth/execution-payloads-benchmarks",
            "DISPATCH_EXPB_BRANCH": "main",
            "DISPATCH_DELAY_SECONDS": "0",
            "DISPATCH_AMOUNT": "25",
            "DISPATCH_ADDITIONAL_EXTRA_FLAGS": "",
            "DISPATCH_FLAT_WRITE_BUFFER_FLOOR": "off",
            "DISPATCH_EXPB_ENV": "",
            "DISPATCH_REBUILD_DOCKER": "true",
            "DISPATCH_RUN_COUNT": "1",
            "DISPATCH_DOCKER_IMAGES": "vendor/client:latest",
            "DISPATCH_ENABLE_RETROSPECTIVE": "false",
            "DISPATCH_RETROSPECTIVE_LAST": "100",
            "DISPATCH_RETROSPECTIVE_STEP": "10",
            "DISPATCH_DOTTRACE": "false",
            "DISPATCH_PERF": "false",
            "DISPATCH_TRACE_BLOCKS": "",
            "DISPATCH_CLIENT_ENV": "",
        }
        values.update(overrides)
        proc, output, _ = self.run_body(self.resolver, values)
        return proc.returncode, proc.stdout + proc.stderr, output

    def run_matrix(self, docker_images):
        proc, output, _ = self.run_body(
            self.matrix,
            {
                "DOCKER_IMAGES": docker_images,
                "LAST": "100",
                "STEP": "10",
                "RUN_COUNT": "1",
            },
        )
        return proc.returncode, proc.stdout + proc.stderr, output

    def run_capability_probe(self, installer, fail_help):
        start = installer.index("requested_flags=()")
        end = installer.index('echo "$(uv tool dir --bin)" >> "${GITHUB_PATH}"')
        probe = installer[start:end]
        with tempfile.TemporaryDirectory(prefix="expb-capability-test-") as temp_dir:
            temp_path = Path(temp_dir)
            mock = temp_path / "expb-mock"
            observed_args = temp_path / "args"
            mock.write_text(
                """#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$@" > "${MOCK_ARGS}"
if [[ "${1:-}" != "execute-scenarios" ]]; then
  echo "unexpected command" >&2
  exit 3
fi
shift
if [[ "${MOCK_FAIL_HELP}" == "true" ]]; then
  echo "No such option: --no-client-metrics" >&2
  exit 2
fi
if [[ "$#" -ne 3 || "$1" != "--perf" || "$2" != "--no-client-metrics" || "$3" != "--help" ]]; then
  echo "unexpected capability probe arguments" >&2
  exit 3
fi
""",
                encoding="utf-8",
            )
            mock.chmod(0o755)
            proc, _, _ = self.run_body(
                'expb_bin="${MOCK_EXPB}"\nexpb_source="mock-source"\n' + probe,
                {
                    "PERF": "true",
                    "MEASUREMENT_SOURCE": "engine-api",
                    "MOCK_EXPB": to_bash(mock),
                    "MOCK_ARGS": to_bash(observed_args),
                    "MOCK_FAIL_HELP": "true" if fail_help else "false",
                },
            )
            return proc, proc.stdout + proc.stderr, observed_args.read_text(encoding="utf-8").splitlines()

    def test_reth_amd64_selects_snapshot_and_common_timing(self):
        code, log, output = self.run_resolver(DISPATCH_CLIENT="reth")
        self.assertEqual(0, code, log)
        self.assertEqual("reth", output["client"])
        self.assertEqual("engine-api", output["measurement_source"])
        self.assertEqual("/mnt/sda/reth-25490000", output["client_snapshot_dir"])
        self.assertEqual("/execution-data", output["snapshot_mount_path"])
        self.assertEqual("false", output["rebuild_docker"])

    def test_capability_probe_uses_parser_status_and_reports_missing_flags(self):
        for installer in self.installers:
            with self.subTest(installer=installer[:40]):
                proc, log, observed_args = self.run_capability_probe(installer, False)
                self.assertEqual(0, proc.returncode, log)
                self.assertEqual(
                    ["execute-scenarios", "--perf", "--no-client-metrics", "--help"],
                    observed_args,
                )

                proc, log, observed_args = self.run_capability_probe(installer, True)
                self.assertNotEqual(0, proc.returncode)
                self.assertIn(
                    "Installed expb from mock-source does not support requested capability flag(s): --perf --no-client-metrics.",
                    log,
                )
                self.assertEqual(
                    ["execute-scenarios", "--perf", "--no-client-metrics", "--help"],
                    observed_args,
                )

    def test_arm_reth_selects_reth_snapshot(self):
        code, log, output = self.run_resolver(DISPATCH_CLIENT="reth", DISPATCH_ARCH="arm64")
        self.assertEqual(0, code, log)
        self.assertEqual("reproducible-benchmarks-arm", output["runner_label"])
        self.assertEqual("/data/reth/reth-25490000", output["client_snapshot_dir"])

    def test_reference_clients_require_flat_layout(self):
        for client in ("reth", "geth"):
            with self.subTest(client=client):
                code, log, _ = self.run_resolver(
                    DISPATCH_CLIENT=client, DISPATCH_STATE_LAYOUT="halfpath"
                )
                self.assertNotEqual(0, code)
                self.assertIn("requires state_layout=flat", log)

    def test_arm_geth_is_rejected(self):
        code, log, _ = self.run_resolver(DISPATCH_CLIENT="geth", DISPATCH_ARCH="arm64")
        self.assertNotEqual(0, code)
        self.assertIn("client=geth requires arch=amd64", log)

    def test_non_nethermind_requires_fusaka_and_explicit_images(self):
        cases = (
            ({"DISPATCH_PAYLOAD_SET": "realblocks"}, "payload_set=fusaka only"),
            ({"DISPATCH_DOCKER_IMAGES": ""}, "requires explicit docker_images"),
        )
        for overrides, expected in cases:
            with self.subTest(expected=expected):
                code, log, _ = self.run_resolver(DISPATCH_CLIENT="reth", **overrides)
                self.assertNotEqual(0, code)
                self.assertIn(expected, log)

    def test_nethermind_default_preserves_sse_and_flatdb_behavior(self):
        code, log, output = self.run_resolver(
            DISPATCH_CLIENT="nethermind",
            DISPATCH_FLAT_WRITE_BUFFER_FLOOR="67108864",
        )
        self.assertEqual(0, code, log)
        self.assertEqual("nethermind", output["client"])
        self.assertEqual("auto", output["measurement_source"])
        self.assertEqual("", output["client_snapshot_dir"])
        self.assertEqual("", output["snapshot_mount_path"])
        self.assertIn("FlatDb.PersistenceWriteBufferFloor=67108864", output["additional_extra_flags"])

    def test_empty_explicit_image_list_is_rejected_before_image_output(self):
        code, log, output = self.run_matrix(" , , ")
        self.assertNotEqual(0, code)
        self.assertIn("must contain at least one non-empty image reference", log)
        self.assertNotIn("images", output)

    def test_duplicate_tags_keep_full_images_under_distinct_ids(self):
        # Two arms sharing a tag must stay two arms: the campaign keys samples by id, never by tag.
        code, log, output = self.run_matrix("a/x:latest,b/x:latest,a/x:latest")
        self.assertEqual(0, code, log)
        images = json.loads(output["images"])
        self.assertEqual(
            ["a/x:latest", "b/x:latest", "a/x:latest"],
            [entry["image"] for entry in images],
        )
        self.assertEqual(["latest"] * 3, [entry["tag"] for entry in images])
        ids = [entry["id"] for entry in images]
        self.assertEqual(len(ids), len(set(ids)))
        for index, image_id in enumerate(ids, start=1):
            self.assertRegex(image_id, r"^image-{}-[0-9a-f]{{12}}$".format(index))

    def run_analyzer(self, body, log_text, metrics_name="expb-metrics-latest-0-run1/metrics.env", **overrides):
        """Run one `Analyze benchmark output` body over a synthetic expb run log."""
        with tempfile.TemporaryDirectory(prefix="expb-analyze-test-") as temp_dir:
            temp_path = Path(temp_dir)
            run_log = temp_path / "expb-run.log"
            run_log.write_text(log_text, encoding="utf-8")
            output = temp_path / "github-output"
            output.touch()
            summary = temp_path / "step-summary.md"
            summary.touch()
            env = dict(os.environ)
            env.update(
                {
                    "RAW_RUN_LOG": to_bash(run_log),
                    "RUNNER_TEMP": to_bash(temp_path),
                    "TAG": "latest-0",
                    "RUN": "1",
                    "MEASUREMENT_SOURCE": "engine-api",
                    "MEASUREMENT_MODE": "standard",
                    "EXPECTED_AMOUNT": "3",
                    "GITHUB_OUTPUT": to_bash(output),
                    "GITHUB_STEP_SUMMARY": to_bash(summary),
                }
            )
            env.update(overrides)
            script = temp_path / "analyze.sh"
            script.write_text(body, encoding="utf-8")
            proc = subprocess.run(
                [self.bash, "--noprofile", "--norc", to_bash(script)],
                env=env,
                cwd=REPO,
                capture_output=True,
                text=True,
            )
            metrics = temp_path / metrics_name
            return proc, metrics.read_text(encoding="utf-8") if metrics.is_file() else ""

    @staticmethod
    def k6_table(rows):
        """The per-payload metrics table expb prints, which is the engine-api timing source."""
        return "".join("| {} | 30000000 | {}.0 |\n".format(index, 20 + index) for index in range(1, rows + 1))

    def test_engine_api_gate_refuses_a_partial_run(self):
        # The gate is the only thing keeping a truncated run from being averaged into a cross-client
        # comparison, and it can abort a multi-hour benchmark. The campaign driver applies the same
        # delivery gate to every multi-image sample (see scripts/expb/test_sequential_driver.py).
        for analyzer in self.analyzers:
            with self.subTest(copy="single"):
                complete, _ = self.run_analyzer(analyzer, self.k6_table(3))
                self.assertEqual(0, complete.returncode, complete.stdout + complete.stderr)

                partial, metrics = self.run_analyzer(analyzer, self.k6_table(2))
                self.assertNotEqual(0, partial.returncode)
                self.assertIn("refusing a partial engine-api measurement", partial.stdout)
                if metrics:
                    self.assertIn("ERROR=partial_engine_api_metrics", metrics)

                # The same short run is a valid measurement when the caller did not ask for the
                # common Engine API timing source.
                auto, _ = self.run_analyzer(analyzer, self.k6_table(2), MEASUREMENT_SOURCE="auto")
                self.assertEqual(0, auto.returncode, auto.stdout + auto.stderr)

    def test_the_feed_pairs_with_the_k6_rows_only_when_complete_or_one_short(self):
        # Same rule as collect_metrics in scripts/expb/sequential_driver.py: the feed can lack only its last
        # record, so a larger gap sits mid-run and the paired figures must not be computed from it.
        for analyzer in self.analyzers:
            for records, paired in ((3, True), (2, True), (1, False)):
                with self.subTest(records=records):
                    feed = "".join(
                        "[payload-server] client_metric block_number={} processing_ms=20\n".format(100 + index)
                        for index in range(records)
                    )
                    proc, metrics = self.run_analyzer(
                        analyzer, feed + self.k6_table(3), metrics_name="expb-metrics.env", MEASUREMENT_SOURCE="auto",
                        # grep -P, which reads the feed, refuses a non-UTF-8 locale such as a bare Git Bash.
                        LC_ALL="C.UTF-8",
                    )
                    self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
                    self.assertIn("SOURCE=sse", metrics)
                    self.assertEqual(paired, "\nMGAS_S=" in "\n" + metrics)
                    self.assertEqual(paired, "OUTSIDE_AVG=" in metrics)
                    self.assertIn("TTFB_MGAS_S=", metrics)

    def test_an_unusable_amount_disables_the_gate_but_says_so(self):
        # `expected_amount` is `.amount // ""` from the rendered config, so it can arrive empty - and
        # a silently skipped completeness check is exactly what the gate exists to prevent.
        for analyzer in self.analyzers:
            for amount in ("", "all"):
                with self.subTest(copy="single", amount=amount):
                    proc, _ = self.run_analyzer(analyzer, self.k6_table(2), EXPECTED_AMOUNT=amount)
                    self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
                    self.assertIn("completeness check is disabled", proc.stdout)

    def test_no_metrics_at_all_still_fails_before_the_completeness_gate(self):
        for analyzer in self.analyzers:
            with self.subTest(copy="single"):
                proc, metrics = self.run_analyzer(analyzer, "nothing parseable here\n")
                self.assertNotEqual(0, proc.returncode)
                self.assertIn("Could not extract any processing_ms data", proc.stdout)
                if metrics:
                    self.assertIn("ERROR=no_metrics", metrics)

    def run_snapshot_preflight(self, client, build):
        """Run the reference-client snapshot preflight against a synthetic snapshot directory."""
        with tempfile.TemporaryDirectory(prefix="expb-snapshot-test-") as temp_dir:
            temp_path = Path(temp_dir)
            source = build(temp_path)
            env = dict(os.environ)
            env.update(
                {
                    "CLIENT": client,
                    "SNAPSHOT_SOURCE": to_bash(source) if source else "",
                    "SNAPSHOT_MOUNT_PATH": "/snapshot",
                }
            )
            script = temp_path / "preflight.sh"
            script.write_text(self.snapshot_preflight, encoding="utf-8")
            return subprocess.run(
                [self.bash, "--noprofile", "--norc", to_bash(script)],
                env=env,
                cwd=REPO,
                capture_output=True,
                text=True,
            )

    def test_snapshot_preflight_rejects_a_snapshot_that_cannot_serve_the_client(self):
        # This preflight runs before a multi-hour benchmark; a wrong or absent snapshot must stop it
        # here rather than surface as a client that never reaches head.
        def absent(_root):
            return None

        def empty(root):
            directory = root / "snapshot"
            directory.mkdir()
            return directory

        def with_child(name):
            def build(root):
                directory = root / "snapshot"
                (directory / name).mkdir(parents=True)
                (directory / "_snapshot_metadata.json").write_text('{"head":25490000}', encoding="utf-8")
                return directory

            return build

        for client, build, expected in (
            ("reth", absent, "does not exist on this runner"),
            ("reth", empty, "does not contain the db directory"),
            ("geth", empty, "does not contain chaindata contents"),
            ("geth", with_child("db"), "does not contain chaindata contents"),
        ):
            with self.subTest(client=client, rejected=expected):
                proc = self.run_snapshot_preflight(client, build)
                self.assertNotEqual(0, proc.returncode, proc.stdout + proc.stderr)
                self.assertIn(expected, proc.stdout + proc.stderr)

        for client, directory in (("reth", "db"), ("geth", "chaindata")):
            with self.subTest(client=client, accepted=directory):
                proc = self.run_snapshot_preflight(client, with_child(directory))
                self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
                self.assertIn("Snapshot provenance: client={}".format(client), proc.stdout)
                self.assertIn("_snapshot_metadata.json", proc.stdout)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
