# Opt-in prepared receipt-migration fixture

This diagnostic harness does not modify either immutable image, receipt flags, security policy, or the protected snapshot. Default RPC workflows are unchanged. `prepare_snapshot=true` on `run-rpc-benchmarks.yml` selects **only** the new baseline preparation job; resolve/benchmark/private corpus jobs are skipped. This preparation is separately authorized for 2026-10-29, latest start01:30UTC, finish-by05:47:34UTC. It is not a primary benchmark.

## Storage and safety contract

One generation lives at `/mnt/sda/expb-data/rpc-prepared/getlogs-<GitHub run id>` on the **actual preparation host**. It retains `owner.json`, upper/work directories, read-only merged mount, evidence, and atomic `ready.json`. The original `/mnt/sda/nethermind-flat-25490000` is a mandatory continuing dependency; this is not a standalone portable copy. No TB-scale copy, hard links, metacopy, redirect, index, or volatile overlay is used. `redirect_dir=off,metacopy=off,index=off` are explicit. Failed mounts do not fall back to different options.

Before touching dataset overlay data, record filesystem/kernel/mount evidence and run a tiny fixture on that filesystem: deletion/whiteouts, opaque-directory replacement, file replacement, sync/unmount/remount, read-only remount and independent nested child isolation. Hosted Actions runs the same tiny real-kernel test. Failure blocks preparation. No unrelated container is stopped or reaped; any running container blocks the actual preparation.

Require512GiB initially free (observed ~303GiB host availability decrease +128GiB reserve +margin), continuously retain128GiB scratch/2GiB root and100,000free inodes. A watchdog checks every10seconds through preparation, validation and sealing. These are floor checks, not a reservation against unrelated host writers. Migration gets10800seconds, health600seconds, plus3600seconds planned reserve. Deadline-aware refusal occurs before allocation; a later pressure/cutoff failure aborts and preserves evidence.

Readiness is not migration completion. Require ordered fresh positive start/completion messages, correct chain1/head25490000/hash, all100frozen ordered-response hashes/counts, exit0/noOOM, `All DBs closed`, sync, read-only remount and unchanged original tripwire. The pinned migration writes its completion byte before its success log. This is the user-approved completion provenance, **not native raw-marker rereading**. `ready.json` is atomically fsynced only after all checks. Physical preparation/sealing is separate from both-image benchmark qualification.

Fingerprints cover recursive path/type/size/mtime/mode/owner/symlink metadata plus CURRENT/IDENTITY/MANIFEST/OPTIONS content hashes, **not full DB contents or a TB-scale checksum**. The sealed manifest records the exact host/path/mount/options/config/image/source/oracle and evidence hashes. Metadata-only tripwires are not protection against an adversary able to rewrite arbitrary host files. Original/derived trees must not be independently written.

## Verify/reuse across arms and runs

Preserve the printed `READY.json` artifact and its `manifest_sha256` externally; never discover trust by accepting an arbitrary file's own hash.

```sh
python3 scripts/rpc-bench/prepared-snapshot.py verify \
  --root /mnt/sda/expb-data/rpc-prepared/getlogs-RUNID \
  --manifest-sha256 FROZEN_READY_JSON_SHA256
```

`verify` refuses missing/partial manifests, changed host/source/path/flags/mount/read-only state or original/prepared tripwires. A different runner with the same label is **not** equivalent. This check does not create/reopen/remount anything.

Before any timing, separately authorize compatibility for each immutable image. The `qualify` command creates a fresh writable child over the same read-only merged fixture, checks exact identity/no renewed migration, compares the100case frozen oracle, cleanly stops, then re-verifies original+prepared fixture. It never mutates or inherits the other image's child. No timed load is executed:

```sh
# GITHUB_RUN_ID must be a unique numeric ownership identity for this authorized job.
python3 scripts/rpc-bench/prepared-snapshot.py qualify \
  --root /mnt/sda/expb-data/rpc-prepared/getlogs-PREP_RUNID \
  --manifest-sha256 FROZEN_READY_JSON_SHA256 \
  --image nethermindeth/nethermind@sha256:IMAGE_DIGEST \
  --output /absolute/owned/compatibility-results
```

Only the two existing immutable image digests are accepted. Each result binds image+fixture-manifest hash+runtime identity+100case parity; no new migration is accepted. An absent migration log alone never qualifies: the independent sealed provenance and verified lower fixture are mandatory. Primary benchmark dispatch/integration and effective native I/O parity remain separate parent-owned gates; neither a sealed fixture nor this CLI automatically authorizes timing. Fresh processes/children do not imply cold OS caches.

## Retention, reboot and deletion

A valid sealed fixture is **user-authorized persistent dataset storage**. Ordinary temporary benchmark cleanup, final-hour cleanup and deadline cancellation must not delete it. Keep it on the recorded host for future authorized runs even if no candidate/primary batch fits tonight. `upper_allocated_bytes` records allocation at seal; shared source extents and total host free space are separate measurements. Read-only mounts are not background migration processes.

There is no auto-remount/retry or auto-retirement. After reboot the missing mount fails `verify`. Recovery must be explicitly operator-reviewed on the same host: verify original tripwire/owner/dependencies against the externally pinned ready manifest, ensure no writer/mount at the target, mount the **same** lower/upper/work with the same explicit options and read-only state, and run `verify` with the original external manifest hash. Do not regenerate anchors or a new ready manifest to hide drift. Kernel/backing-filesystem changes require rerunning the tiny compatibility probe before recovery. Unsupported whiteouts/xattrs, changed paths, missing upper/work or mount identity fail closed. Never treat the upper alone as a full dataset or merge it with plain `cp`.

Incomplete generations are quarantined, not ready. Stop only the recorded owned container with bounded grace; no forced shutdown can publish readiness. Preserve failed logs and owner paths. An incomplete read-only mount may be retained for manual triage; it must never be substituted for a ready generation.

Manual retirement requires user/parent authorization and exact recorded host/path/owner verification, no active child or DB writer, successful ordinary unmounts (children before prepared merged), then removal of **only** that generation's upper/work/merged/evidence/manifest tree. Never delete the protected source, broad prefixes, or busy mounts; never lazy/force-unmount then recursively delete. The harness intentionally has no broad cleanup command.
