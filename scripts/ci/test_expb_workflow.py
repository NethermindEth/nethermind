#!/usr/bin/env python3
"""Regression tests for the EXPB workflow's single and multi-image paths."""

import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


def find_bash():
    """Use Git Bash on Windows; System32 bash.exe would launch WSL instead."""
    if os.name != "nt":
        return shutil.which("bash")
    git = shutil.which("git")
    if git:
        candidate = Path(git).parent.parent / "bin" / "bash.exe"
        if candidate.exists():
            return str(candidate)
    found = shutil.which("bash")
    return found if found and "System32" not in found else None


def to_bash(path):
    """Convert a native path to the form understood by Git Bash."""
    if os.name != "nt":
        return str(path)
    text = str(path).replace("\\", "/")
    return "/" + text[0].lower() + text[2:] if len(text) > 1 and text[1] == ":" else text


REPO = Path(__file__).resolve().parents[2]
WORKFLOW = REPO / ".github" / "workflows" / "run-expb-reproducible-benchmarks.yml"

JOB_PATTERN = re.compile(
    r"(?ms)^  (?P<name>[A-Za-z0-9_-]+):[^\r\n]*\r?\n"
    r"(?P<body>.*?)(?=^  [A-Za-z0-9_-]+:[^\r\n]*(?:\r?\n|\Z)|\Z)"
)
STEP_PATTERN = re.compile(
    r"(?ms)^      - name: (?P<name>[^\r\n]+)\r?\n"
    r"(?P<body>.*?)(?=^      - |\Z)"
)


class ExpbWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = WORKFLOW.read_text(encoding="utf-8")
        cls.jobs = {
            match["name"]: match["body"]
            for match in JOB_PATTERN.finditer(cls.workflow)
        }

    @classmethod
    def step(cls, job_name, name_fragment):
        fragment = name_fragment.casefold()
        for step in STEP_PATTERN.finditer(cls.jobs[job_name]):
            if fragment in step["name"].casefold():
                return step["body"]
        raise AssertionError(f"No step containing {name_fragment!r} in {job_name}")

    @classmethod
    def run_text(cls, job_name, name_fragment):
        step = cls.step(job_name, name_fragment)
        marker = re.search(r"(?m)^        run:\s*\|\s*$", step)
        if marker is None:
            raise AssertionError(f"{job_name}/{name_fragment} is not a shell step")
        body = []
        for line in step[marker.end() :].splitlines():
            if not line.strip():
                body.append("")
            elif line.startswith(" " * 10):
                body.append(line[10:])
            else:
                break
        return "\n".join(body) + "\n"

    @classmethod
    def step_field(cls, job_name, name_fragment, field):
        step = cls.step(job_name, name_fragment)
        match = re.search(rf"(?m)^        {re.escape(field)}:\s*(.*)$", step)
        if match is None:
            match = re.search(rf"(?m)^          {re.escape(field)}:\s*(.*)$", step)
        if match is None:
            raise AssertionError(f"No {field!r} field in {job_name}/{name_fragment}")
        return match.group(1)

    def run_warmup_validation(self, log, expected_status):
        bash = find_bash()
        if bash is None:  # pragma: no cover - environment guard
            self.skipTest("a POSIX bash is required to execute the workflow snippet")

        analyze = self.run_text("benchmark", "Analyze benchmark output")
        start = analyze.index('warmup_status="not-requested"')
        end = analyze.index('\ngrep -in "Exception"', start)
        warmup_logic = analyze[start:end]

        with tempfile.TemporaryDirectory(prefix="expb-warmup-") as directory:
            root = Path(directory)
            log_path = root / "run.log"
            log_path.write_text(log, encoding="utf-8")
            script = f'''\
set -euo pipefail
clean_log="$1"
RUNNER_TEMP="$2"
MEASUREMENT_MODE=compute-warm
{warmup_logic}
printf 'status=%s\\n' "$warmup_status"
'''
            result = subprocess.run(
                [bash, "--noprofile", "--norc", "-eo", "pipefail", "-c", script,
                 "warmup", to_bash(log_path), to_bash(root)],
                capture_output=True,
                text=True,
                cwd=directory,
                timeout=10,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(f"status={expected_status}", result.stdout.strip().splitlines()[-1])

    def test_compute_warm_runtime_comparison_is_numeric_and_strict(self):
        rows = "\n".join(f"| {index} | 1 | 1.0 |" for index in (9, 10, 100))
        all_success = rows + "\n" + "\n".join(
            f"[payload-server] warmup block={index} ok elapsed=1.0ms" for index in (9, 10, 100)
        )
        self.run_warmup_validation(all_success, "ok")

        missing = rows + "\n" + "\n".join(
            f"[payload-server] warmup block={index} ok elapsed=1.0ms" for index in (9, 10)
        )
        missing += "\nordinary warmup block=100 ok elapsed=1.0ms\n"
        self.run_warmup_validation(missing, "missing")

        failed = rows + "\n" + "\n".join(
            f"[payload-server] warmup block={index} ok elapsed=1.0ms" for index in (9, 10)
        )
        failed += "\n[payload-server] warmup block=100 FAILED elapsed=1.0ms error=rpc\n"
        self.run_warmup_validation(failed, "failed")

    def test_single_job_keeps_measurement_quality_and_profiling(self):
        run = self.run_text("benchmark", "Run expb scenarios")
        self.assertIn("expb execute-scenarios", run)
        self.assertRegex(run, r"(?m)^\s*tee\s+.*RAW_RUN_LOG")
        self.assertIn("RAW_RUN_LOG", run)

        analyze = self.step("benchmark", "Analyze benchmark output")
        self.assertEqual("analyze", self.step_field("benchmark", "Analyze benchmark output", "id"))
        analyze_run = self.run_text("benchmark", "Analyze benchmark output")
        self.assertIn('metrics_file="${RUNNER_TEMP}/expb-metrics.env"', analyze_run)
        self.assertIn('>> "${GITHUB_OUTPUT}"', analyze_run)

        quality = self.run_text("benchmark", "Enforce run quality gates")
        self.assertIn("steps.run-expb.outcome", quality)
        self.assertRegex(quality, r"exception_found.*true")
        self.assertIn("exit 1", quality)

        self.assertEqual(
            "collect-profiling",
            self.step_field("benchmark", "Collect and upload profiling artifacts", "id"),
        )
        profiling = self.run_text("benchmark", "Collect and upload profiling artifacts")
        self.assertIn("dotnet-trace", profiling)
        self.assertIn("perf", profiling)

    def test_single_job_uploads_run_logs_even_after_failure(self):
        upload = self.step("benchmark", "Upload benchmark logs")
        self.assertEqual("always()", self.step_field("benchmark", "Upload benchmark logs", "if"))
        self.assertIn("upload-artifact", self.step_field("benchmark", "Upload benchmark logs", "uses"))
        artifact_path = self.step_field("benchmark", "Upload benchmark logs", "path").casefold()
        self.assertIn("logs", artifact_path)

        stage = self.run_text("benchmark", "Stage benchmark logs")
        self.assertIn("METRICS_FILE", stage)
        self.assertIn("metrics.env", stage)
        self.assertIn("RENDERED_CONFIG_FILE", stage)
        self.assertIn("rendered-config.yaml", stage)

    def test_compute_warm_requires_payload_server_warmup_for_each_k6_index(self):
        analyze = self.step("benchmark", "Analyze benchmark output")
        analyze_run = self.run_text("benchmark", "Analyze benchmark output")
        self.assertRegex(analyze, r"(?m)^          MEASUREMENT_MODE:")
        self.assertIn(r"\[payload-server\]", analyze_run)
        self.assertIn(r"warmup[[:space:]]+block=[0-9]+", analyze_run)
        self.assertRegex(analyze_run, r"(?i)warmup.*(?:ok|failed|missing)")
        self.assertRegex(analyze_run, r"(?i)(?:measured|delivered|k6).*payload.*indices?")
        self.assertRegex(analyze_run, r"(?i)warmup.*indices?")
        self.assertRegex(analyze_run, r"(?i)(?:comm|diff|sort).*(?:warmup|payload)")

        quality = self.run_text("benchmark", "Enforce run quality gates")
        self.assertRegex(quality, r"(?i)compute-warm.*warmup_status")
        self.assertRegex(quality, r"(?i)warmup_status.*!=.*ok")

    def test_default_explicitly_pins_unset_expb_branch(self):
        install = self.run_text("benchmark", "Install or upgrade expb")
        self.assertRegex(install, r"expb_[a-z_]*revision\s*=\s*['\"]?[0-9a-f]{40}")
        self.assertRegex(
            install,
            r"(?is)(?:elif|if).*EXPB_REPO.*execution-payloads-benchmarks.*"
            r"expb_source=.*@\$\{expb_[a-z_]*revision\}",
        )

    def test_multi_job_runs_one_sequential_campaign_without_matrix(self):
        multi = self.jobs["benchmark-multi"]
        self.assertNotRegex(multi, r"(?m)^    strategy:")

        self.assertNotRegex(multi, r"\$\{\{\s*matrix\.")

        campaign = self.run_text("benchmark-multi", "Run sequential EXPB campaign")
        self.assertIn("sequential_driver.py", campaign)
        self.assertRegex(campaign, r"\bpython3\s+.*sequential_driver\.py")

        upload = self.step("benchmark-multi", "Upload campaign artifact")
        self.assertEqual("always()", self.step_field("benchmark-multi", "Upload campaign artifact", "if"))
        self.assertIn("upload-artifact", self.step_field("benchmark-multi", "Upload campaign artifact", "uses"))

    def test_resolved_duplicate_images_get_distinct_ids(self):
        resolve = self.run_text("resolve-images", "Resolve Docker images")

        # The array index makes two equal image references separate campaign entries;
        # the digest keeps each generated id stable for that input position.
        self.assertRegex(resolve, r"image_id=.*\$\(\(i\s*\+\s*1\)\)")
        self.assertIn("digest=", resolve)
        self.assertIn("images_json=", resolve)
        self.assertRegex(resolve, r"\$current\s*\+\s*\[\{id:\s*\$id")
        self.assertIn('echo "images=${images_json}"', resolve)


if __name__ == "__main__":
    unittest.main()
