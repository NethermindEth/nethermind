#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression suite for the "Reap stale expb overlay mounts" workflow step.

The step is destructive root-level shell (`umount`, `rm -rf`) that runs unattended on the two
benchmark runners, and it is duplicated verbatim across three checkout-less jobs. So this suite

  * extracts the step body from the shipped YAML instead of re-implementing it, and asserts the
    three copies are byte-identical, and
  * drives that body against synthetic mount tables, with `sudo` and `umount` replaced by shell
    functions and the mount table passed positionally, so the guards that keep `rm -rf` inside
    the benchmark data dir are exercised rather than argued about.

The body runs under the options GitHub gives a `shell: bash` step - `--noprofile --norc -eo
pipefail` - because `set -e` is on there and the body cannot turn it off.

Run with: python -m unittest discover -s scripts/ci -p 'test*.py'
Runs on Linux and, via Git Bash, on Windows.
"""

import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
STEP_NAME = "Reap stale expb overlay mounts"
HOLDER_STEP_NAME = "Reap leftover benchmark containers and networks"
WORKFLOWS = [
    REPO / ".github/workflows/arm-runner-maintenance.yml",
    REPO / ".github/workflows/run-expb-reproducible-benchmarks.yml",
]

# `sudo` and `umount` as shell functions rather than PATH stubs, so the suite needs no writable
# bin dir and behaves the same wherever bash runs. The fake umount edits the mount table the step
# reads, which is what makes the stacked-mount and still-mounted cases meaningful.
PREAMBLE = r"""
MOUNTS_TABLE="$1"   # the same synthetic table the step takes positionally
sudo() { "$@"; }
umount() {
  local lazy=0
  if [[ "$1" == "-l" ]]; then lazy=1; shift; fi
  local t="$1"
  printf '%s %s\n' "${lazy}" "${t}" >> "${UMOUNT_LOG}"   # every attempt, so order is assertable
  case ":${FAIL_ALL:-}:" in *":${t}:"*) return 32 ;; esac
  if [[ "${lazy}" -eq 0 ]]; then
    case ":${FAIL_PLAIN:-}:" in *":${t}:"*) return 32 ;; esac
  fi
  # Pop the top layer only, like the real umount: drop the last line with this target.
  awk -v t="${t}" '{ l[NR] = $0; if ($2 == t) last = NR }
                   END { for (i = 1; i <= NR; i++) if (i != last) print l[i] }' \
      "${MOUNTS_TABLE}" > "${MOUNTS_TABLE}.new" \
    && mv "${MOUNTS_TABLE}.new" "${MOUNTS_TABLE}"
}
"""


def extract_step_bodies(path, step_name):
    """Return the dedented `run:` block of every step called *step_name* in *path*."""
    lines = path.read_bytes().decode("utf-8").replace("\r\n", "\n").split("\n")
    bodies = []
    i = 0
    while i < len(lines):
        header = re.match(r"^(\s*)-\s+name:\s+(.*?)\s*$", lines[i])
        i += 1
        if not header or header.group(2) != step_name:
            continue
        step_indent = len(header.group(1))
        while i < len(lines):
            if re.match(r"^\s{0,%d}-\s+name:" % step_indent, lines[i]):
                break
            run = re.match(r"^(\s+)run:\s*\|\s*$", lines[i])
            i += 1
            if not run:
                continue
            block_indent = len(run.group(1)) + 2
            body = []
            while i < len(lines):
                line = lines[i]
                if line.strip() == "":
                    body.append("")
                elif len(line) - len(line.lstrip(" ")) >= block_indent:
                    body.append(line[block_indent:])
                else:
                    break
                i += 1
            while body and body[-1] == "":
                body.pop()
            bodies.append("\n".join(body) + "\n")
            break
    return bodies


def mount_line(target, upper, work, lower="/snapshot", fstype="overlay"):
    """One /proc/self/mounts record in the shape the kernel writes for an overlay."""
    return "expb-executor-1 {} {} rw,relatime,lowerdir={},upperdir={},workdir={} 0 0".format(
        target, fstype, lower, upper, work
    )


def find_bash():
    """A POSIX bash. On Windows that means Git Bash - System32 bash.exe launches WSL instead."""
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
    """Native path -> the form bash sees, so the step under test parses real-looking paths."""
    if os.name != "nt":
        return str(path)
    text = str(path).replace("\\", "/")
    return "/" + text[0].lower() + text[2:] if len(text) > 1 and text[1] == ":" else text


def to_native(path):
    """Inverse of `to_bash`, for asserting on the filesystem from Python."""
    if os.name == "nt" and re.match(r"^/[a-zA-Z]/", path):
        return Path(path[1] + ":/" + path[3:])
    return Path(path)


class ReapStepTestCase(unittest.TestCase):
    """Base fixture: one throwaway data dir, the shipped step body, and a runner for it."""

    @classmethod
    def setUpClass(cls):
        cls.bash = find_bash()
        if cls.bash is None:  # pragma: no cover - environment guard
            raise unittest.SkipTest("a POSIX bash is required to run the workflow step body")
        cls.bodies = [b for wf in WORKFLOWS for b in extract_step_bodies(wf, STEP_NAME)]
        if len(cls.bodies) != 3:
            raise AssertionError(
                "expected 3 copies of the {!r} step, found {}".format(STEP_NAME, len(cls.bodies))
            )

    def setUp(self):
        native_box = Path(tempfile.mkdtemp(prefix="reap-test-")).resolve()
        self.addCleanup(shutil.rmtree, native_box, ignore_errors=True)
        self.native_box = native_box
        self.box = to_bash(native_box)
        self.data_dir = self.box + "/expb-data"
        self.snapshot = to_native(self.data_dir) / "snapshot"
        self.snapshot.mkdir(parents=True)
        (self.snapshot / "keep.sst").write_text("precious")

    def overlay(self, name="work"):
        """Create expb's `<data-dir>/<name>/{merged,upper,work}`; returns the three bash paths."""
        base = self.data_dir + "/" + name
        for leaf in ("merged", "upper", "work"):
            (to_native(base) / leaf).mkdir(parents=True, exist_ok=True)
        (to_native(base) / "upper" / "dirty.sst").write_text("cancelled run's writes")
        return base + "/merged", base + "/upper", base + "/work"

    def assertGone(self, bash_path):
        self.assertFalse(to_native(bash_path).exists(), bash_path + " should have been reclaimed")

    def assertKept(self, bash_path):
        self.assertTrue(to_native(bash_path).exists(), bash_path + " should have been left alone")

    def run_step(self, mounts, data_dir=None, fail_plain=(), fail_all=(), body=None):
        """Run the step body over *mounts*; returns (stdout, mounts left, [[lazy, target], ...])."""
        mounts_file = self.native_box / "mounts"
        mounts_file.write_bytes(("\n".join(mounts) + "\n").encode("utf-8"))
        umount_log = self.native_box / "umount.log"
        umount_log.write_bytes(b"")
        env = dict(os.environ)
        env.update(
            UMOUNT_LOG=to_bash(umount_log),
            FAIL_PLAIN=":".join(fail_plain),
            FAIL_ALL=":".join(fail_all),
            EXPB_DATA_DIR=self.data_dir if data_dir is None else data_dir,
        )
        proc = subprocess.run(
            # The options GitHub gives a `shell: bash` step (`bash --noprofile --norc -eo pipefail
            # {0}`): the body's own `set -uo pipefail` adds `u` but cannot clear `-e`, so without
            # these the suite would exercise a laxer shell than the runner does. Then `$0` and the
            # mount table as `$1`, the way the step reads it.
            [self.bash, "--noprofile", "--norc", "-eo", "pipefail", "-c",
             PREAMBLE + (self.bodies[0] if body is None else body),
             "reap", to_bash(mounts_file)],
            env=env,
            capture_output=True,
            text=True,
            cwd=str(self.native_box),
        )
        self.assertEqual(
            0,
            proc.returncode,
            "the step is best-effort and must never fail a benchmark job:\n" + proc.stderr,
        )
        remaining = [l for l in mounts_file.read_text().splitlines() if l.strip()]
        unmounted = [l.split(" ", 1) for l in umount_log.read_text().splitlines() if l.strip()]
        return proc.stdout, remaining, unmounted


class CopiesInSync(ReapStepTestCase):
    def test_the_holder_reap_covers_every_container_that_can_hold_a_swept_overlay(self):
        """The sweep is data-dir-wide, so the reap ahead of it must not stop at expb's own names."""
        lists = []
        for wf in WORKFLOWS:
            for body in extract_step_bodies(wf, HOLDER_STEP_NAME):
                match = re.search(r"^\s*for filter in (.*); do$", body, re.M)
                self.assertIsNotNone(match, "no filter loop in {}".format(wf.name))
                lists.append(match.group(1).split())
        self.assertEqual(3, len(lists), "expected one holder reap per job")
        for names in lists[1:]:
            self.assertEqual(lists[0], names, "the holder reap filter lists have drifted")
        for filter_ in ("name=expb", "label=expb", "name=rpcbench-", "name=nethermind-rpcbench"):
            self.assertIn(filter_, lists[0])

    def test_the_mount_table_seam_is_not_reachable_from_the_job_environment(self):
        """A fabricated table both picks the targets and certifies the unmount, so keep it to a
        positional - Actions invokes a `run:` block with no arguments after the script path."""
        self.assertIn('MOUNTS="${1:-/proc/self/mounts}"', self.bodies[0])
        self.assertNotIn("MOUNTS_FILE", self.bodies[0])

    def test_all_three_copies_are_byte_identical(self):
        """Sharing the body would need a checkout none of the three jobs has; this is the guard."""
        for i, body in enumerate(self.bodies[1:], start=2):
            self.assertEqual(
                self.bodies[0], body, "copy {} of the {!r} step has drifted".format(i, STEP_NAME)
            )

    def test_every_copy_behaves_the_same(self):
        merged, upper, work = self.overlay()
        for i, body in enumerate(self.bodies, start=1):
            with self.subTest(copy=i):
                out, remaining, _ = self.run_step([mount_line(merged, upper, work)], body=body)
                self.assertIn("unmounted " + merged, out)
                self.assertEqual([], remaining)


class RootGuards(ReapStepTestCase):
    def test_empty_data_dir_skips_instead_of_anchoring_at_root(self):
        merged, upper, work = self.overlay()
        out, remaining, unmounted = self.run_step([mount_line(merged, upper, work)], data_dir="")
        self.assertIn("EXPB_DATA_DIR is empty", out)
        self.assertEqual([], unmounted)
        self.assertKept(upper)
        self.assertEqual(1, len(remaining))

    def test_shallow_data_dir_is_refused(self):
        for root in ("/", "//", "/data", "/data/.."):
            with self.subTest(root=root):
                out, _, unmounted = self.run_step([], data_dir=root)
                self.assertIn("too shallow", out)
                self.assertEqual([], unmounted)

    def test_trailing_slash_data_dir_still_sweeps(self):
        merged, upper, work = self.overlay()
        out, remaining, _ = self.run_step(
            [mount_line(merged, upper, work)], data_dir=self.data_dir + "/"
        )
        self.assertIn("unmounted " + merged, out)
        self.assertGone(upper)
        self.assertEqual([], remaining)


class ScratchPathGuards(ReapStepTestCase):
    def test_upperdir_reaching_the_data_dir_itself_is_refused(self):
        """A bare `$root/*` glob also matched `$root/`, whose rm -rf would take the snapshots."""
        merged, _, work = self.overlay()
        for upper in (self.data_dir, self.data_dir + "/", self.data_dir + "/."):
            with self.subTest(upperdir=upper):
                out, _, _ = self.run_step([mount_line(merged, upper, work)])
                self.assertIn("refusing overlay scratch path outside", out)
                self.assertKept(self.data_dir + "/snapshot/keep.sst")

    def test_traversal_out_of_the_data_dir_is_refused(self):
        merged, _, work = self.overlay()
        outside = self.box + "/outside"
        to_native(outside).mkdir()
        (to_native(outside) / "keep").write_text("not ours")
        out, _, _ = self.run_step([mount_line(merged, self.data_dir + "/work/../../outside", work)])
        self.assertIn("refusing overlay scratch path outside", out)
        self.assertKept(outside + "/keep")

    def test_absolute_path_outside_the_data_dir_is_refused(self):
        merged, _, work = self.overlay()
        out, _, _ = self.run_step([mount_line(merged, "/etc", work)])
        self.assertIn("refusing overlay scratch path outside", out)

    def test_sibling_sharing_the_data_dir_prefix_is_not_a_descendant(self):
        merged, _, work = self.overlay()
        out, _, _ = self.run_step([mount_line(merged, self.data_dir + "-elsewhere/upper", work)])
        self.assertIn("refusing overlay scratch path outside", out)

    def test_octal_escaped_path_is_refused_rather_than_guessed(self):
        merged, _, work = self.overlay()
        escaped = self.data_dir + "/my" + chr(92) + "040dir/upper"
        out, _, _ = self.run_step([mount_line(merged, escaped, work)])
        self.assertIn("refusing escaped overlay scratch path", out)


class Unmounting(ReapStepTestCase):
    def test_scratch_reclaimed_and_mountpoint_left_for_expb_to_reuse(self):
        merged, upper, work = self.overlay()
        _, remaining, unmounted = self.run_step([mount_line(merged, upper, work)])
        self.assertEqual([], remaining)
        self.assertEqual([["0", merged]], unmounted, "a plain unmount must be tried first")
        self.assertGone(upper)
        self.assertGone(work)
        self.assertKept(merged)
        self.assertKept(self.data_dir + "/snapshot/keep.sst")

    def test_lazy_unmount_is_only_the_fallback(self):
        merged, upper, work = self.overlay()
        _, remaining, unmounted = self.run_step(
            [mount_line(merged, upper, work)], fail_plain=[merged]
        )
        self.assertEqual([["0", merged], ["1", merged]], unmounted)
        self.assertEqual([], remaining)
        self.assertGone(upper)

    def test_a_mount_that_will_not_unmount_keeps_its_scratch(self):
        merged, upper, work = self.overlay()
        out, remaining, _ = self.run_step([mount_line(merged, upper, work)], fail_all=[merged])
        self.assertIn("is still mounted", out)
        self.assertKept(upper)
        self.assertEqual(1, len(remaining))

    def test_nested_mounts_unwind_deepest_first(self):
        outer, outer_up, outer_wk = self.overlay("work")
        inner, inner_up, inner_wk = self.overlay("work/merged/nested")
        _, remaining, unmounted = self.run_step(
            [mount_line(outer, outer_up, outer_wk), mount_line(inner, inner_up, inner_wk)]
        )
        self.assertEqual([inner, outer], [t for _, t in unmounted])
        self.assertEqual([], remaining)

    def test_stacked_mounts_on_one_target_are_fully_unwound(self):
        merged, upper_a, work_a = self.overlay("work")
        _, upper_b, work_b = self.overlay("work-second")
        _, remaining, unmounted = self.run_step(
            [mount_line(merged, upper_a, work_a), mount_line(merged, upper_b, work_b)]
        )
        self.assertEqual([merged, merged], [t for _, t in unmounted], "both layers must be popped")
        self.assertEqual([], remaining)
        for scratch in (upper_a, work_a, upper_b, work_b):
            self.assertGone(scratch)

    def test_a_non_overlay_layer_stacked_on_a_swept_overlay_is_unwound_too(self):
        """`still_mounted` matches any fstype on purpose: an unmount that leaves a bind or tmpfs
        behind has not freed the mountpoint. Only the overlay layer's own scratch is deleted.

        The overlay must be the LAST line on the target: `umount` pops the top layer, so this is the
        ordering that leaves a non-overlay residue behind. Scoping `still_mounted` to the overlay fstype
        then reports the mountpoint free and deletes the scratch under a live mount - with the lines the
        other way round the surviving overlay line masks that, and the mutation goes unnoticed."""
        merged, upper, work = self.overlay("work")
        _, tmpfs_upper, tmpfs_work = self.overlay("work-tmpfs")
        _, remaining, unmounted = self.run_step(
            [
                mount_line(merged, tmpfs_upper, tmpfs_work, fstype="tmpfs"),
                mount_line(merged, upper, work),
            ]
        )
        self.assertEqual([merged, merged], [t for _, t in unmounted])
        self.assertEqual([], remaining)
        self.assertGone(upper)
        self.assertGone(work)
        self.assertKept(tmpfs_upper)
        self.assertKept(tmpfs_work)

    def test_an_escaped_mount_target_is_refused_rather_than_reported_unmounted(self):
        """`awk -v` decodes the octal escape, so still_mounted cannot see the line: without the
        guard the step announced an unmount it never attempted and reclaimed a live overlay."""
        _, upper, work = self.overlay("work")
        escaped = self.data_dir + "/my" + chr(92) + "040dir/merged"
        out, remaining, unmounted = self.run_step([mount_line(escaped, upper, work)])
        self.assertIn("refusing escaped overlay mount target", out)
        self.assertNotIn("unmounted ", out)
        self.assertEqual([], unmounted)
        self.assertEqual(1, len(remaining))
        self.assertKept(upper)
        self.assertKept(work)

    def test_stacked_mounts_keep_every_layer_when_one_will_not_unmount(self):
        """The top layer detaches but a lower one does not: nothing on that target may be deleted."""
        merged, upper_a, work_a = self.overlay("work")
        _, upper_b, work_b = self.overlay("work-second")
        out, remaining, _ = self.run_step(
            [mount_line(merged, upper_a, work_a), mount_line(merged, upper_b, work_b)],
            fail_all=[merged],
        )
        self.assertIn("is still mounted", out)
        self.assertEqual(2, len(remaining))
        for scratch in (upper_a, work_a, upper_b, work_b):
            self.assertKept(scratch)


class Scoping(ReapStepTestCase):
    def test_docker_and_foreign_overlays_are_left_alone(self):
        merged, upper, work = self.overlay()
        docker = mount_line(
            "/var/lib/docker/overlay2/abc/merged", "/var/lib/docker/x", "/var/lib/docker/y"
        )
        foreign = mount_line("/srv/other/merged", "/srv/other/upper", "/srv/other/work")
        _, remaining, unmounted = self.run_step([mount_line(merged, upper, work), docker, foreign])
        self.assertEqual([merged], [t for _, t in unmounted])
        self.assertEqual([docker, foreign], remaining)

    def test_non_overlay_mounts_under_the_data_dir_are_left_alone(self):
        bind = mount_line(
            self.data_dir + "/bound", self.data_dir + "/x", self.data_dir + "/y", fstype="ext4"
        )
        _, remaining, unmounted = self.run_step([bind])
        self.assertEqual([], unmounted)
        self.assertEqual([bind], remaining)

    def test_the_drift_dump_is_capped_without_tripping_errexit(self):
        """Capped inside awk: piping into `head` risked SIGPIPE aborting the step under errexit."""
        many = [
            mount_line(
                "/srv/other/merged-{:05d}".format(i),
                "/srv/other/upper-{:05d}".format(i),
                "/srv/other/work",
            )
            for i in range(4000)
        ]
        out, remaining, unmounted = self.run_step(many)
        self.assertEqual([], unmounted)
        self.assertEqual(len(many), len(remaining))
        dumped = [l for l in out.splitlines() if "unmatched overlay:" in l]
        self.assertEqual(20, len(dumped))

    def test_nothing_to_reap_dumps_unmatched_overlays_for_drift(self):
        foreign = mount_line("/srv/other/merged", "/srv/other/upper", "/srv/other/work")
        docker = mount_line(
            "/var/lib/docker/overlay2/abc/merged", "/var/lib/docker/x", "/var/lib/docker/y"
        )
        out, remaining, unmounted = self.run_step([foreign, docker])
        self.assertIn("No stale expb overlay mounts under", out)
        self.assertIn("unmatched overlay: /srv/other/merged", out)
        self.assertNotIn("/var/lib/docker", out, "docker's own overlays are not drift")
        self.assertEqual([], unmounted)
        self.assertEqual([foreign, docker], remaining)


if __name__ == "__main__":
    unittest.main()
