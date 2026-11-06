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
    def run_publish(self, version_file="", *, dockerfile="Dockerfile", tag="test", ref="refs/heads/test",
                    event="workflow_dispatch", image="nethermind"):
        action = ACTION.read_text(encoding="utf-8")
        script = action.split("      run: |\n", 1)[1]
        script = re.sub(r"(?m)^        ", "", script)
        mocks = r"""
docker() { printf 'DOCKER_ARG=%s\n' "$@"; }
git() { printf '1793836800\n'; }
"""
        scratch_root = "C:/Temp" if os.name == "nt" else None
        with tempfile.TemporaryDirectory(dir=scratch_root) as scratch:
            environment = dict(
                os.environ, IMAGE_NAME=image, TAG=tag,
                DOCKERFILE=dockerfile, VERSION_FILE=version_file,
                BUILD_CONFIG="release", PLATFORMS="linux/amd64,linux/arm64",
                ADD_MASTER_TAG="true", GITHUB_REF=ref,
                GITHUB_EVENT_NAME=event, GITHUB_SHA="a" * 40,
                GITHUB_OUTPUT=Path(scratch, "output").as_posix(), CI="true",
            )
            shell = (str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe")
                     if os.name == "nt" else shutil.which("bash"))
            result = subprocess.run(
                [shell, "-e"], input=(mocks + script).encode(), cwd=REPO_ROOT,
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
        for project, dockerfile, tag, expected in (
            ("src/Nethermind/Directory.Build.props", "Dockerfile", "test", execution_version),
            (LIGHT_CLIENT_PROJECT, "tools/LightClient/Dockerfile", "light-client-1.0.0", "1.0.0"),
            ("./" + LIGHT_CLIENT_PROJECT, "./tools/LightClient/Dockerfile", "light-client-1.0.0", "1.0.0"),
            ("", "./tools/../tools/LightClient/Dockerfile", "light-client-1.0.0", "1.0.0"),
        ):
            with self.subTest(project=project):
                result = self.run_publish(project, dockerfile=dockerfile, tag=tag)
                output = result.stdout + result.stderr
                self.assertEqual(result.returncode, 0, output)
                self.assertIn(f"DOCKER_ARG=VERSION={expected}\n", output)
                self.assertIn(f"DOCKER_ARG=nethermindeth/nethermind:{tag}\n", output)
                self.assertIn("DOCKER_ARG=--push\n", output)

    def test_light_client_inputs_cannot_overwrite_execution_tags(self):
        for tag in ("latest", "performance", "test", "master", "master-abcdef0", "nightly", "light-client-2.0.0", ""):
            for ref in ("refs/heads/test", "refs/heads/master"):
                with self.subTest(tag=tag, ref=ref):
                    result = self.run_publish(LIGHT_CLIENT_PROJECT, dockerfile="tools/LightClient/Dockerfile", tag=tag, ref=ref)
                    self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertNotIn("DOCKER_ARG=", result.stdout)

    def test_products_cannot_publish_each_others_version_or_tags(self):
        for dockerfile, project, tag in (
            ("tools/LightClient/Dockerfile", "src/Nethermind/Directory.Build.props", "light-client-1.0.0"),
            ("Dockerfile", LIGHT_CLIENT_PROJECT, "test"),
            ("Dockerfile", LIGHT_CLIENT_PROJECT, "nightly"),
            ("Dockerfile", "", "light-client-1.0.0"),
            ("tools/Bootnode/Dockerfile", "", "light-client-1.0.0"),
        ):
            with self.subTest(dockerfile=dockerfile, project=project, tag=tag):
                result = self.run_publish(project, dockerfile=dockerfile, tag=tag, ref="refs/heads/master")
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertNotIn("DOCKER_ARG=", result.stdout)

    def test_light_client_push_on_master_does_not_add_execution_master_alias(self):
        result = self.run_publish(LIGHT_CLIENT_PROJECT, dockerfile="tools/LightClient/Dockerfile", tag="light-client-1.0.0",
                                  ref="refs/heads/master", event="push")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn("DOCKER_ARG=nethermindeth/nethermind:master-", result.stdout)

    def test_execution_tag_policy_is_preserved(self):
        for tag, ref, event, allowed, master_alias in (
            ("latest", "refs/heads/release/1.0", "workflow_dispatch", True, False),
            ("performance", "refs/heads/performance", "push", True, False),
            ("", "refs/heads/test/branch", "push", True, False),
            ("nightly", "refs/heads/master", "schedule", True, False),
            ("master", "refs/heads/master", "push", True, True),
            ("nightly", "refs/heads/test", "workflow_dispatch", False, False),
            ("master", "refs/heads/test", "push", False, False),
            ("master-abcdef0", "refs/heads/test", "push", False, False),
        ):
            with self.subTest(tag=tag, ref=ref):
                result = self.run_publish(tag=tag, ref=ref, event=event)
                self.assertEqual(result.returncode == 0, allowed, result.stdout + result.stderr)
                self.assertEqual("DOCKER_ARG=--push\n" in result.stdout, allowed)
                self.assertEqual("DOCKER_ARG=nethermindeth/nethermind:master-aaaaaaa\n" in result.stdout, master_alias)

    def test_other_product_keeps_its_version_and_repository(self):
        result = self.run_publish("tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj",
                                  dockerfile="tools/Bootnode/Dockerfile", tag="1.0.0", image="bootnode")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("DOCKER_ARG=nethermindeth/bootnode:1.0.0\n", result.stdout)
        self.assertIn("DOCKER_ARG=VERSION=1.0.0\n", result.stdout)

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
