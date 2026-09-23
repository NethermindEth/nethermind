#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Exercise both Bootnode release gates without publishing artifacts or using the network."""

import os
import re
import subprocess
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/release-bootnode.yml"
MOCK_COMMANDS = r"""
sudo() { :; }
xmlstarlet() { printf '%s\n' "$VERSION_PREFIX"; }
git() {
  case "$1" in
    check-ref-format) command git "$@" ;;
    ls-remote)
      echo TAG_LOOKUP >&2
      [[ "$*" == "ls-remote --refs origin refs/tags/bootnode-$VERSION_PREFIX" ]] || return 99
      [[ "$TAG_STATE" != error ]] || return 128
      if [[ "$TAG_STATE" != absent ]]; then
        printf '%s\t%s\n' "$TAG_STATE" refs/tags/bootnode-1.0.0
      fi
      ;;
    *) return 99 ;;
  esac
}
jq() {
  python3 -c 'import json, sys
value = json.load(sys.stdin)[sys.argv[1].removeprefix(".")]
print(json.dumps(value) if isinstance(value, bool) else value)' "$2"
}
gh() {
  case "$1 $2" in
    'release view')
      [[ "$RELEASE_EXISTS" == true ]] || return 1
      if [[ "${4:-}" == --json ]]; then
        [[ "$5" == isDraft,targetCommitish ]] || return 99
        printf '{"isDraft":%s,"targetCommitish":"%s"}\n' "$IS_DRAFT" "$DRAFT_TARGET"
      fi
      ;;
    'release create') RELEASE_EXISTS=true; IS_DRAFT=true; DRAFT_TARGET=$GITHUB_SHA ;;
    'release upload') echo ASSETS_UPLOADED ;;
    'release edit') echo RELEASE_PUBLISHED ;;
    'api repos/NethermindEth/nethermind/commits/'*)
      case "${2##*/commits/}" in
        refs/tags/bootnode-1.0.0|bootnode-1.0.0)
          [[ "$TAG_STATE" != absent ]] || return 1
          [[ "$TAG_COMMIT" != error ]] || return 1
          printf '%s\n' "$TAG_COMMIT"
          ;;
        A|B) printf '%s\n' "$DRAFT_TARGET" ;;
        *) return 1 ;;
      esac
      ;;
    *) return 99 ;;
  esac
}
"""


class BootnodeReleaseTests(unittest.TestCase):
    def run_step(self, step, **values):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        match = re.search(
            rf"(?ms)^      - name: {step}\n.*?^        run: \|\n"
            r"(?P<script>(?:^          [^\n]*\n|^\n)+)", workflow
        )
        self.assertIsNotNone(match, step)
        script = re.sub(r"(?m)^          ", "", match["script"])
        environment = dict(
            os.environ, VERSION_PREFIX="1.0.0", TAG_STATE="absent", TAG_COMMIT="A",
            RELEASE_EXISTS="false", IS_DRAFT="true", DRAFT_TARGET="B",
            GITHUB_SHA="B", GITHUB_REPOSITORY="NethermindEth/nethermind",
            RELEASE_TAG="bootnode-1.0.0", RELEASE_NAME="Bootnode 1.0.0",
            RELEASE_VERSION="1.0.0", PACKAGE_DIR="packages",
            INPUT_PUBLISH_LATEST="false", GITHUB_OUTPUT="/dev/null", GITHUB_ENV="/dev/null",
        )
        environment.update(values)
        return subprocess.run(
            ["bash", "-e"], input=MOCK_COMMANDS + script,
            text=True, capture_output=True, env=environment, timeout=10,
        )

    def test_version_validation(self):
        cases = [
            ("1.0.0", True),
            ("1.10.0", True),
            ("1" * 128, True),
            ("1" * 129, False),
            ("", False),
            (".1.0.0", False),
            ("-1.0.0", False),
            ("1.0.0+build", False),
            ("1.0.0/extra", False),
            ("1.0.0 extra", False),
            ("1.0.0\nextra", False),
            ("1..0", False),
            ("1.0.0.lock", False),
        ]
        for version, succeeds in cases:
            with self.subTest(version=version):
                result = self.run_step("Detect version", VERSION_PREFIX=version)
                output = result.stdout + result.stderr
                self.assertEqual(result.returncode == 0, succeeds, output)
                self.assertEqual("TAG_LOOKUP" in output, succeeds, output)
                if not succeeds:
                    self.assertIn("must produce valid Docker and Git tags", output)

    def test_release_commit_gates(self):
        # An annotated tag's object ID differs from its peeled commit ID.
        cases = [
            ("new release", "absent", "A", "false", "true", "B", True),
            ("new draft", "absent", "A", "true", "true", "B", True),
            ("stale draft", "absent", "A", "true", "true", "A", False),
            ("draft with stale lightweight tag", "A", "A", "true", "true", "B", False),
            ("draft with stale annotated tag", "tag-object", "A", "true", "true", "B", False),
            ("tag overrides stale draft target", "B", "B", "true", "true", "A", True),
            ("matching annotated tag", "tag-object", "B", "true", "true", "A", True),
            ("published rerun", "B", "B", "true", "false", "A", True),
            ("published stale tag", "A", "A", "true", "false", "B", False),
            ("published missing tag", "absent", "A", "true", "false", "B", False),
            ("retained tag without release", "A", "A", "false", "true", "B", False),
            ("matching tag without release", "B", "B", "false", "true", "B", True),
            ("tag lookup failure", "error", "A", "true", "true", "B", False),
            ("tag resolution failure", "A", "error", "true", "true", "B", False),
            ("draft resolution failure", "absent", "A", "true", "true", "error", False),
        ]
        for step in ("Detect version", "Publish"):
            for name, tag_state, tag_commit, exists, draft, target, succeeds in cases:
                with self.subTest(step=step, case=name):
                    result = self.run_step(
                        step, TAG_STATE=tag_state, TAG_COMMIT=tag_commit,
                        RELEASE_EXISTS=exists, IS_DRAFT=draft, DRAFT_TARGET=target,
                    )
                    output = result.stdout + result.stderr
                    self.assertEqual(result.returncode == 0, succeeds, output)
                    if not succeeds:
                        self.assertIn("::error::", output)
                    self.assertEqual("ASSETS_UPLOADED" in output, succeeds and step == "Publish", output)
                    self.assertEqual("RELEASE_PUBLISHED" in output, succeeds and step == "Publish", output)


if __name__ == "__main__":
    unittest.main()
