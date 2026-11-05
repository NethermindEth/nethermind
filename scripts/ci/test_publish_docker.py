#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Exercise Docker image version selection without building or publishing images."""

import os
import re
import shutil
import subprocess
import tempfile
import unittest
from xml.etree import ElementTree
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
ACTION = REPO_ROOT / ".github/actions/publish-docker/action.yaml"
LIGHT_CLIENT_PROJECT = "src/Nethermind/Nethermind.LightClient/Nethermind.LightClient.csproj"


class PublishDockerTests(unittest.TestCase):
    def run_publish(self, version_file):
        action = ACTION.read_text(encoding="utf-8")
        script = action.split("      run: |\n", 1)[1]
        script = re.sub(r"(?m)^        ", "", script)
        mocks = r"""
docker() { printf 'DOCKER_ARG=%s\n' "$@"; }
git() { printf '1793836800\n'; }
"""
        with tempfile.TemporaryDirectory() as scratch:
            environment = dict(
                os.environ, IMAGE_NAME="light-client", TAG="1.0.0",
                DOCKERFILE="tools/LightClient/Dockerfile", VERSION_FILE=version_file,
                BUILD_CONFIG="release", PLATFORMS="linux/amd64,linux/arm64",
                ADD_MASTER_TAG="true", GITHUB_REF="refs/heads/light-client-verified-p2p",
                GITHUB_EVENT_NAME="workflow_dispatch", GITHUB_SHA="a" * 40,
                GITHUB_OUTPUT=Path(scratch, "output").as_posix(), CI="true",
            )
            result = subprocess.run(
                [shutil.which("bash"), "-e"], input=(mocks + script).encode(), cwd=REPO_ROOT,
                env=environment, capture_output=True, timeout=10,
            )
            result.stdout = result.stdout.decode()
            result.stderr = result.stderr.decode()
            return result

    def test_project_version_reaches_image_build(self):
        properties = ElementTree.parse(REPO_ROOT / "src/Nethermind/Directory.Build.props")
        execution_version = properties.findtext(".//VersionPrefix")
        suffix = properties.findtext(".//VersionSuffix")
        if suffix:
            execution_version += f"-{suffix}"
        for project, expected in (
            ("src/Nethermind/Directory.Build.props", execution_version),
            (LIGHT_CLIENT_PROJECT, "1.0.0"),
        ):
            with self.subTest(project=project):
                result = self.run_publish(project)
                output = result.stdout + result.stderr
                self.assertEqual(result.returncode, 0, output)
                self.assertIn(f"DOCKER_ARG=VERSION={expected}\n", output)
                self.assertIn("DOCKER_ARG=nethermindeth/light-client:1.0.0\n", output)
                self.assertIn("DOCKER_ARG=--push\n", output)

    def test_version_file_path_with_spaces(self):
        with tempfile.TemporaryDirectory() as scratch:
            project = Path(scratch, "light client.csproj")
            shutil.copyfile(REPO_ROOT / LIGHT_CLIENT_PROJECT, project)
            result = self.run_publish(project.as_posix())
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("DOCKER_ARG=VERSION=1.0.0\n", result.stdout)

    def test_invalid_version_prevents_publish(self):
        with tempfile.TemporaryDirectory() as scratch:
            project = Path(scratch, "invalid.csproj")
            for declaration in ("", "1.0.0 extra", "1.0.0\nextra"):
                with self.subTest(declaration=declaration):
                    project.write_text(
                        f"<Project><VersionPrefix>{declaration}</VersionPrefix></Project>\n",
                        encoding="utf-8",
                    )
                    result = self.run_publish(project.as_posix())
                    output = result.stdout + result.stderr
                    self.assertNotEqual(result.returncode, 0, output)
                    self.assertNotIn("DOCKER_ARG=", output)
            result = self.run_publish(str(project.with_name("missing.csproj")))
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertNotIn("DOCKER_ARG=", result.stdout)


if __name__ == "__main__":
    unittest.main()
