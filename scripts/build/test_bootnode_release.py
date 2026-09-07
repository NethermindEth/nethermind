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
xmlstarlet() { printf '%s\n' 1.0.0; }
git() {
  case "$1" in
    check-ref-format) return 0 ;;
    ls-remote)
      [[ "$*" == 'ls-remote --refs origin refs/tags/bootnode-1.0.0' ]] || return 99
      [[ "$TAG_STATE" != error ]] || return 128
      if [[ "$TAG_STATE" != absent ]]; then
        printf '%s\t%s\n' "$TAG_STATE" refs/tags/bootnode-1.0.0
      fi
      ;;
    *) return 99 ;;
  esac
}
jq() {
  case "$2" in
    .isDraft) printf '%s\n' "$IS_DRAFT" ;;
    .targetCommitish) printf '%s\n' "$DRAFT_TARGET" ;;
    *) return 99 ;;
  esac
}
gh() {
  case "$1 $2" in
    'release view') [[ "$RELEASE_EXISTS" == true ]] && printf '%s\n' '{}' ;;
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
        workflow = WORKFLOW.read_text(encoding="utf-8")
        for step in ("Detect version", "Publish"):
            match = re.search(
                rf"(?ms)^      - name: {step}\n.*?^        run: \|\n"
                r"(?P<script>(?:^          [^\n]*\n|^\n)+)", workflow
            )
            self.assertIsNotNone(match, step)
            script = re.sub(r"(?m)^          ", "", match["script"])
            for name, tag_state, tag_commit, exists, draft, target, succeeds in cases:
                with self.subTest(step=step, case=name):
                    environment = dict(
                        os.environ, TAG_STATE=tag_state, TAG_COMMIT=tag_commit,
                        RELEASE_EXISTS=exists, IS_DRAFT=draft, DRAFT_TARGET=target,
                        GITHUB_SHA="B", GITHUB_REPOSITORY="NethermindEth/nethermind",
                        RELEASE_TAG="bootnode-1.0.0", RELEASE_NAME="Bootnode 1.0.0",
                        RELEASE_VERSION="1.0.0", PACKAGE_DIR="packages",
                        INPUT_PUBLISH_LATEST="false", GITHUB_OUTPUT="/dev/null", GITHUB_ENV="/dev/null",
                    )
                    result = subprocess.run(
                        ["bash", "--noprofile", "--norc", "-euo", "pipefail"],
                        input=MOCK_COMMANDS + script, text=True, capture_output=True,
                        env=environment, timeout=10,
                    )
                    output = result.stdout + result.stderr
                    self.assertEqual(result.returncode == 0, succeeds, output)
                    if not succeeds:
                        self.assertIn("::error::", output)
                    self.assertEqual("ASSETS_UPLOADED" in output, succeeds and step == "Publish", output)
                    self.assertEqual("RELEASE_PUBLISHED" in output, succeeds and step == "Publish", output)


if __name__ == "__main__":
    unittest.main()
