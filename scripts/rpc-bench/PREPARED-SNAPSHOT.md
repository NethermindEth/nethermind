# Opt-in prepared receipt-migration fixture

This diagnostic harness does not modify either immutable image, receipt flags, security policy, or the protected snapshot. Default RPC workflows are unchanged. `prepare_snapshot=true` on `run-rpc-benchmarks.yml` selects **only** the new baseline preparation job; resolve/benchmark/private corpus jobs are skipped. Current authorization ends 2026-10-30T01:06:04Z; the final hour beginning00:06:04UTC is cleanup/report only. Preparation uses the existing `--finish` bound, now2026-10-30T00:06:04Z. Latest fresh start is derived as finish minus10800s migration,600s health and3600s reserve:2026-10-29T19:56:04Z. Recovery replaces the10800s gate with3600s, retaining600s health and3600s reserve: latest21:56:04Z. The former6b1bc849e preparation run40797353683 was canceled while queued/unassigned; no prepared dataset was created. Its source/request/history remain immutable. This mode is not a primary benchmark.

## Storage and safety contract

One generation lives at `/mnt/sda/expb-data/rpc-prepared/getlogs-<GitHub run id>` on the **actual preparation host**. It retains `owner.json`, upper/work directories, read-only merged mount, evidence, and atomic `ready.json`. The original `/mnt/sda/nethermind-flat-25490000` is a mandatory continuing dependency; this is not a standalone portable copy. No TB-scale copy, hard links, metacopy, redirect, index, or volatile overlay is used. `redirect_dir=off,metacopy=off,index=off` are explicit. Failed mounts do not fall back to different options.

Before touching dataset overlay data, record filesystem/kernel/mount evidence and run a tiny fixture on that filesystem: deletion/whiteouts, opaque-directory replacement, file replacement, sync/unmount/remount, read-only remount and independent nested child isolation. Hosted Actions runs the same tiny real-kernel test. Failure blocks preparation. No unrelated container is stopped or reaped; any running container blocks the actual preparation.

Require512GiB initially free (observed ~303GiB host availability decrease +128GiB reserve +margin), continuously retain128GiB scratch/2GiB root and100,000free inodes. A watchdog checks every10seconds through preparation, validation and sealing. These are floor checks, not a reservation against unrelated host writers. Migration gets10800seconds, health600seconds, plus3600seconds planned reserve. Deadline-aware refusal occurs before allocation; a later pressure/cutoff failure aborts and preserves evidence.

Readiness is not migration completion. Require ordered fresh positive start/completion messages, correct chain1/head25490000/hash, all100frozen ordered-response hashes/counts, exit0 or exit130 only with configured `SIGINT`, noOOM/error/running state, both `All DBs closed` and `Nethermind is shut down`, no exception/invalid/fatal logs, sync, read-only remount and unchanged original tripwire. The pinned migration writes its completion byte before its success log. This is the user-approved completion provenance, **not native raw-marker rereading**. `ready.json` is atomically fsynced only after all checks. Physical preparation/sealing is separate from both-image benchmark qualification.

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

Only the two existing immutable image digests are accepted. Each result binds image+fixture-manifest hash+runtime identity+100case parity; no new migration is accepted. An absent migration log alone never qualifies: the independent sealed provenance and verified lower fixture are mandatory. Primary benchmark dispatch and effective native I/O parity remain separate parent-owned gates; neither a sealed fixture nor this CLI automatically authorizes timing. Fresh processes/children do not imply cold OS caches.

## Optional timed-sweep integration (no primary dispatch implied)

The existing `jsonbench-sweep` accepts `tool_config.prepared_snapshot_root` and `prepared_snapshot_sha256` together. Defaults are empty and preserve all old behavior. Prepared mode is restricted to both original immutable Nethermind images, flat layout, overlay isolation, public/non-corpus config, original five additional flags (same order as preparation), no environment overrides, and fresh migration timeout0. This is a distinct verified-manifest gate, not silent migration-gate disabling.

Before any measured cell, the sweep starts **two separate untimed qualification processes/children**, one per pinned image: exact runtime identity and all100ordered response hashes/counts against the frozen oracle, absence of renewed migration, genuine normal shutdown/noOOM/noKILL, and original/prepared tripwires. Failure on either aborts the sweep before timing without deleting the sealed fixture.

Each later measured arm gets a fresh writable child with the same explicit durable overlay options. Pre-arm manifest/host/source/read-only tripwires and runtime identity/no-remigration are checked. The measured process is **not** warmed with the100case oracle before its first-use phase; qualification used separate processes. After measurement, all100cases are checked again before clean shutdown. Stop-node records exit/OOM/DB-close checks while the container still exists and verifies the lower fixture; source/prepared tripwires are rechecked after teardown. Checks write under each arm state (`prepared-gate`, `prepared-running`, `prepared-stopped.json`). First-use remains fresh-process first-use, never cold OS cache; earlier qualification naturally warms shared host caches.

A10second sweep watchdog checks disk/inode reserves, deadline budget, active container liveness and any renewed receipt migration. Fixture/gate operations are bounded to5minutes each. Failure/cancellation still runs owned stop/verification; prepared mode never uses lazy unmounts or broad prefix reaping. These harness processes and probes add matched overhead, disclosed rather than treated as free.

Required workflow knobs, in addition to the ordinary immutable public anchor config/tool/image pins:

```json
{
  "prepared_snapshot_root": "/mnt/sda/expb-data/rpc-prepared/getlogs-PREP_RUNID",
  "prepared_snapshot_sha256": "EXTERNALLY_FROZEN_READY_JSON_SHA256",
  "eth_call_corpus": false,
  "state_layout": "flat",
  "db_isolation_all": "overlay",
  "receipt_hashes_migration_timeout": 0,
  "migration_latest_start_utc": "2026-10-29T20:00:00Z",
  "migration_finish_by_utc": "2026-10-30T00:06:04Z"
}
```

`node_config.scratch_root` must be a **new uniquely owned** `/mnt/sda/expb-data/rpc-prepared-benchmark-EXPERIMENTID`, outside the persistent generation and original. Use both exact pinned image entries in `clients`, each suffixed with `+--LogIndex.Enabled=false;--Receipt.StoreReceipts=true;--Receipt.CompactReceiptStore=true;--Receipt.StoreTxHashes=true;--Receipt.DeriveFromState=false`. Preserve corpus ref/config/seed/CPU/memory/resources; explicitly budget both extra qualification startups before the planned complete primary batch. UTC cutoffs above illustrate this authorization only, not proof any batch fits. No measured arm may start without at least the existing1hour teardown reserve plus its health budget; the workflow retains TERM/KILL/artifact grace. Parent must reject a batch that cannot fit in full, rather than treat partial cycles as the planned experiment.

This integration does not solve native backend observability or authorize an IO-equality claim. The parent must separately resolve/approve that outstanding experimental limitation before any primary dispatch. Both-image compatibility artifacts are bound to the same externally pinned fixture; no valid ready fixture is destroyed if an image fails.

## Explicit reviewed quarantine recovery (untimed)

Only run40815701914 on `core-ethereum-mainnet-snapshots-0` is eligible. Its82904byte artifact12881330426 has zip SHA256`9e220699c61df86cbdb83968d0c71208bb1a8ccf1f709f52794ed79d21e9afe9`. It completed migration and100oracle queries, then cleanly closed DBs and shut down with configuredSIGINT/exit130; the old harness incorrectly required exit0. This is not proof of present readiness.

Dispatch the existing preparation workflow with `prepare_snapshot=true` and `node_config={"prepared_snapshot_recover":true}`. This selects recovery only, never a fallback fresh migration. `actions/download-artifact` retrieves the exact producer artifact/run with an actions-read token. The CLI independently pins the canonical filename→SHA256 map of all extracted artifact files (`cdbed476f6180334e8d4f32eb56921936626fd15a05922202f7f1f84a2c4dac8`). Preserve original failure/artifact/owner permanently.

`recover --root /mnt/sda/expb-data/rpc-prepared/getlogs-40815701914 --recovery-evidence EXTRACTED_OLD_ARTIFACT` rejects foreign host/root/owner, an existing ready manifest, missing or changed source, missing/nonreadonly/different overlay mount/options, old configuration/runtime/oracle drift, missing positive old migration/clean shutdown, running containers, child mounts and busy processes. It never recreates mounts or promotes absence of migration logs by itself. `findmnt` can omit explicit-off options matching kernel defaults: omitted redirect_dir/metacopy/index require read-only `/sys/module/overlay/parameters/*` evidence ofN/0; missing, unknown or enabled defaults fail closed. Explicit on/volatile options are rejected; redirect_dir=nofollow is the stricter disabled form. Linux maps the requested redirect_dir=off to reported follow when `/sys/module/overlay/parameters/redirect_always_follow` isY/1; only this independently verified kernel policy permits reported follow (creation still disabled). Unknown/missing policy fails closed; no module parameter is written. The same-kernel toy records actual mount reporting and omitted-option defaults for the known explicit-off command; lower/upper/work paths still match verbatim. The original source remains a lower only, never remounted writable.

After all provenance checks and the tiny real-kernel probe (including in-placeRO→RW→RO and independent child isolation), remount only the owned generation writable. A fresh pinned baseline process must show the identical runtime identity, no new migration,100exact oracle results and genuine clean shutdown. Sync and remount readonly, recheck source metadata/control tripwire, then publish a new ready seal with new run/source and old producer/artifact lineage. Any failure remains quarantined; no force/lazy remount or new migration fallback. Recovery retains128GiB scratch/2GiB root/100kinodes continuously, not the fresh512GiB initial guard: the~300GiB migrated upper already exists. Record disk planning, stop work within1h or finish-minus30minutes (whichever is earlier), leaving bounded180s shutdown and reserve. Both-image fresh-child qualification remains mandatory and separate before timing. No timing is authorized by recovery.

## Retention, reboot and deletion

A valid sealed fixture is **user-authorized persistent dataset storage**. Ordinary temporary benchmark cleanup, final-hour cleanup and deadline cancellation must not delete it. Keep it on the recorded host for future authorized runs even if no candidate/primary batch fits tonight. `upper_allocated_bytes` records allocation at seal; shared source extents and total host free space are separate measurements. Read-only mounts are not background migration processes.

There is no auto-remount/retry or auto-retirement. After reboot the missing mount fails `verify`. Recovery must be explicitly operator-reviewed on the same host: verify original tripwire/owner/dependencies against the externally pinned ready manifest, ensure no writer/mount at the target, mount the **same** lower/upper/work with the same explicit options and read-only state, and run `verify` with the original external manifest hash. Do not regenerate anchors or a new ready manifest to hide drift. Kernel/backing-filesystem changes require rerunning the tiny compatibility probe before recovery. Unsupported whiteouts/xattrs, changed paths, missing upper/work or mount identity fail closed. Never treat the upper alone as a full dataset or merge it with plain `cp`.

Incomplete generations are quarantined, not ready. Stop only the recorded owned container with bounded grace; no forced shutdown can publish readiness. Preserve failed logs and owner paths. An incomplete read-only mount may be retained for manual triage; it must never be substituted for a ready generation.

Manual retirement requires user/parent authorization and exact recorded host/path/owner verification, no active child or DB writer, successful ordinary unmounts (children before prepared merged), then removal of **only** that generation's upper/work/merged/evidence/manifest tree. Never delete the protected source, broad prefixes, or busy mounts; never lazy/force-unmount then recursively delete. The harness intentionally has no broad cleanup command.
