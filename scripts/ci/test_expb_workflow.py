#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Exercise the immutable image provenance step in the EXPB workflow."""

import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
WORKFLOW = REPO / ".github/workflows/run-expb-reproducible-benchmarks.yml"
DIGEST = "sha256:" + "a" * 64
REVISION = "b" * 40


def extract_step(path, step_name):
    lines = path.read_bytes().decode("utf-8-sig").replace("\r\n", "\n").split("\n")
    for index, line in enumerate(lines):
        header = re.match(r"^\s*- name: (?P<name>.*?)\s*$", line)
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
        return "\n".join(body) + "\n"
    raise AssertionError("workflow step {!r} was not found".format(step_name))


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
    if os.name != "nt":
        return str(path)
    text = str(path).replace("\\", "/")
    return "/" + text[0].lower() + text[2:] if len(text) > 1 and text[1] == ":" else text


def parse_output(path):
    values = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            values[key] = value
    return values


class ExpbWorkflowImageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bash = find_bash()
        if cls.bash is None:  # pragma: no cover - environment guard
            raise unittest.SkipTest("a POSIX bash is required to execute workflow steps")
        cls.resolve_image = extract_step(WORKFLOW, "Resolve immutable image and revision")
        cls.render_config = extract_step(WORKFLOW, "Render benchmark config")

    def run_resolver(self, expected_revision="", actual_revision=REVISION):
        with tempfile.TemporaryDirectory(prefix="expb-image-test-") as directory:
            root = Path(directory)
            output = root / "github-output"
            output.touch()
            docker_args = root / "docker-args"
            bin_dir = root / "bin"
            bin_dir.mkdir()
            docker = bin_dir / "docker"
            docker.write_text(
                "#!/usr/bin/env bash\n"
                "set -euo pipefail\n"
                "printf '%s\\n' \"$*\" >> \"${DOCKER_ARGS_LOG}\"\n"
                "if [[ \"${1:-} ${2:-} ${3:-} ${4:-}\" == \"buildx imagetools inspect repo/image:tag\" ]]; then\n"
                f"  printf 'Name: repo/image:tag\\nDigest: {DIGEST}\\n'\n"
                "elif [[ \"$1\" == pull ]]; then\n"
                "  printf 'pulled %s\\n' \"$2\"\n"
                "elif [[ \"$1\" == run ]]; then\n"
                f"  printf 'Nethermind\\nCommit: {actual_revision}\\n'\n"
                "else\n"
                "  exit 2\n"
                "fi\n",
                encoding="utf-8",
            )
            docker.chmod(0o755)
            script = root / "resolve-image.sh"
            script.write_text(self.resolve_image, encoding="utf-8")
            env = dict(os.environ)
            env.update(
                IMAGE_LABEL="repo/image:tag",
                EXPECTED_REVISION=expected_revision,
                GITHUB_OUTPUT=to_bash(output),
                DOCKER_ARGS_LOG=to_bash(docker_args),
                PATH=str(bin_dir) + os.pathsep + env["PATH"],
            )
            result = subprocess.run(
                [self.bash, "--noprofile", "--norc", "-eo", "pipefail", to_bash(script)],
                cwd=REPO,
                env=env,
                capture_output=True,
                text=True,
            )
            return result, parse_output(output), result.stdout + result.stderr, docker_args.read_text(encoding="utf-8")

    def test_reuse_resolves_immutable_digest_and_reports_image_commit(self):
        result, output, log, docker_args = self.run_resolver()
        self.assertEqual(0, result.returncode, log)
        self.assertEqual(f"repo/image:tag@{DIGEST}", output["image"])
        self.assertEqual(REVISION, output["revision"])
        self.assertIn("run --rm --network none --platform linux/amd64 --entrypoint /nethermind/nethermind", docker_args)

    def test_dispatched_image_commit_mismatch_fails_before_benchmark(self):
        expected = "c" * 40
        result, output, log, _ = self.run_resolver(expected_revision=expected)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("reports commit", log)
        self.assertEqual({}, output)

    def test_single_image_render_uses_prepare_output_instead_of_mutable_tag(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("DOCKER_TAG: ${{ needs.prepare-docker.outputs.image }}", workflow)
        self.assertIn("NETHERMIND_IMAGE: ${{ needs.prepare-docker.outputs.image }}", workflow)
        self.assertIn("image_revision: ${{ steps.resolve-image.outputs.revision }}", workflow)

    def test_rendered_config_contains_the_exact_immutable_image_reference(self):
        image = "repo/image:tag@" + DIGEST
        with tempfile.TemporaryDirectory(prefix="expb-render-test-") as directory:
            root = Path(directory)
            source = root / "config.yaml"
            rendered = root / "rendered.yaml"
            output = root / "github-output"
            source.write_text(
                "scenarios:\n  nethermind:\n    image: nethermindeth/nethermind:<<DOCKER_TAG>>\n    amount: <<AMOUNT>>\nmetadata:\n  image: <<DOCKER_TAG>>\n",
                encoding="utf-8",
            )
            output.touch()
            bin_dir = root / "bin"
            bin_dir.mkdir()
            curl = bin_dir / "curl"
            curl.write_text(
                "#!/usr/bin/env bash\n"
                "set -euo pipefail\n"
                "while [[ $# -gt 0 ]]; do\n"
                "  if [[ \"$1\" == -o ]]; then out=\"$2\"; shift 2; else shift; fi\n"
                "done\n"
                "printf '#!/usr/bin/env bash\\nexit 0\\n' > \"${out}\"\n",
                encoding="utf-8",
            )
            curl.chmod(0o755)
            script = root / "render-config.sh"
            script.write_text(self.render_config, encoding="utf-8")
            env = dict(
                os.environ,
                SOURCE_CONFIG_FILE=to_bash(source),
                RENDERED_CONFIG_FILE=to_bash(rendered),
                DOCKER_TAG=image,
                IMAGE_REVISION=REVISION,
                DELAY_SECONDS="0",
                AMOUNT="1",
                PAYLOAD_SET="fusaka",
                SCENARIO_NAME="nethermind-flat-fusaka-test",
                ADDITIONAL_EXTRA_FLAGS="",
                TRACE_BLOCKS="",
                CLIENT_ENV="",
                EXPB_ENV_PASSTHROUGH="",
                MEASUREMENT_MODE="standard",
                RUNNER_TEMP=to_bash(root),
                EXPB_DATA_DIR="/mnt/sda/expb-data",
                FLAT_SNAPSHOT_DIR="/mnt/sda/nethermind-flat-snapshot",
                FLAT_SNAPSHOT_BLOCK_DIR="/mnt/sda/nethermind-flat-25490000",
                GITHUB_OUTPUT=to_bash(output),
                PATH=str(bin_dir) + os.pathsep + os.environ["PATH"],
            )
            result = subprocess.run(
                [self.bash, "--noprofile", "--norc", "-eo", "pipefail", to_bash(script)],
                cwd=REPO,
                env=env,
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            rendered_text = rendered.read_text(encoding="utf-8")

        self.assertIn(f"image: {image}", rendered_text)
        self.assertIn(f"  image: {image}\n", rendered_text)
        self.assertNotIn("<<DOCKER_TAG>>", rendered_text)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
