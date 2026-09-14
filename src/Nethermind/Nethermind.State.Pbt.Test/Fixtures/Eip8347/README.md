# EIP-8347 independent reference fixtures

Generated from **CPerezz/go-ethereum `e31a37fb88c2b75c0897c033bd3f4279dee42268`**, against EIPs repository `f3079a09e8c606afcb0e5e1a309ff228b88dc067` (7523, 8297, 8347, 7928, 8159). No Nethermind state implementation generated the expected roots.

## Contents

- `genesis.json`: exact MPT-at-genesis chain, PBT activation timestamp 48.
- `scenario.json`: participant identities and operation sequence.
- `blocks.json`: actual reference Engine-produced block/header RLP, hash/parent, transaction bytes/hashes, BAL RLP/hash, both state roots and commitment selector. `anchor` has no BAL; subsequent blocks carry authenticated BALs.
- `states/index.json`, `states/*.alloc.json`: complete logical post-state enumerated from the reference chain's genesis-plus-BAL key universe. Each reconstructed MPT root was checked against the independently recorded MPT root.
- `images/*/{snapshot.pbt,preimages.bin}` and `images-manifest.json`: **legacy geth interoperability images**, not canonical EIP preimages. Each state was converted by geth and checked against the actual follower/canonical PBT root, then verified with `importState(verifyOnly:true)`. Whole-file Keccak digests are in the manifest.
- `canonical/*/{snapshot.pbt,preimages.bin}` and `canonical-manifest.json`: current pinned EIP format. Snapshot bytes match the verified geth snapshots; a user-approved test-only adapter emits fixed-width, Keccak-path-ordered preimages. A separate bounded parser checks exact keys/order/counts/EOF against the exported logical state and rejects truncation/trailing bytes. Legacy preimage decoding independently confirms the same source key set.
- `reference/`: test-only collectors, original reference vectors, commands and verification evidence.

## Coverage and limits

Eleven states: anchor; A1 fresh balance-only recipient; A2 storage insertion with shared bytecode; A3 delegation installation/boundary parent; A4 first PBT child and zero-storage deletion; A5 delegation clearing; rewind to common A1; alternative B2–B6, re-crossing activation at B4 and outgrowing A. Each expected transaction's exact inclusion and successful receipt is asserted, with logical state checks. The deletion fixture is **storage deletion**, not account deletion. Account-deletion regression coverage remains required in implementation tests.

Before activation the header root is MPT and shadow is PBT. During the transition, header is PBT and debug shadow is MPT. Post-activation images are **parity vectors, not eligible EIP-8347 migration anchors**. The image collector verifies their reconstructed MPT roots using a synthetic MPT header; actual lifecycle identity is separately recorded. The actual genesis CLI conversion/import uses the real stored genesis header.

This fixture branch collector rewinds its Engine-driven builder and re-crosses; independent upstream `TestMigrationBoundaryStraddleReorg` and `TestStraddleHealThroughEnginePayloads` additionally test a victim's sidechain import/heal. No claim is made that these static fixtures replace Nethermind's full E2E or durability tests.

The low-level published EIP8297 vector set contains `zero_basic_data_with_storage`; it is reproduced only as a tree/converter vector. All accounts in migration genesis and exported migration states are checked non-empty under the EIP7523 assumption.

## Approved reference discrepancy

The pinned geth preimage codec is legacy RLP with raw-address/numeric-slot ordering. Current EIP8347 requires fixed 20-byte address, 4-byte big-endian count, full 32-byte slots and Keccak-path ordering. The user approved split oracle evidence: geth execution/conversion verifies state and roots; a narrow test-only encoder/parser verifies current canonical preimage bytes. Nethermind must implement **canonical/**, not the legacy **images/** format. Pinned geth CLI verification only applies to legacy images.

The reference importer calls its Keccak digest reader more than once; `Read` advances the squeeze state, so its final log can show a different digest from its first digest. Manifest digests are fresh whole-file `crypto.Keccak256Hash` results, not copied from that final log. This reference logging issue does not change roots or file contents.

## Reproduction

Use a disposable reference worktree at the pinned revision. Install the three files under `reference/collectors/` into `cmd/geth/` (image/canonical collectors) and `eth/catalyst/` (lifecycle collector). Use Go >=1.24 (execution used the approved Nix Go shell); scope caches/temp/data/output to a disposable directory.

```sh
# From the pinned reference worktree:
go test ./cmd/geth ./eth/catalyst ./trie/bintrie -run 'Test(ConvertMatchesReference|ConvertMatchesEmbedding|AnchorSeededCatchup|BatchImportAcrossTheFork|MigrationBoundaryStraddleReorg|StraddleHealThroughEnginePayloads|MigrationSurvivesRestartPreFork|FullMigrationLifecycle|.*Vector.*)$' -count=1 -v
go build -o "$OUT/geth" ./cmd/geth
EIP8347_FIXTURE_OUT="$OUT/fixtures" go test ./eth/catalyst -run '^TestExportEip8347Lifecycle$' -count=1 -timeout=180s -v
EIP8347_FIXTURE_OUT="$OUT/fixtures" go test ./cmd/geth -run '^TestExportEip8347Images$' -count=1 -v
EIP8347_FIXTURE_OUT="$OUT/fixtures" go test ./cmd/geth -run '^TestExportEip8347Canonical$' -count=1 -v
"$OUT/geth" --datadir "$DATA/cli-anchor" --state.scheme=path --cache.preimages init "$OUT/fixtures/genesis.json"
"$OUT/geth" --datadir "$DATA/cli-anchor" --state.scheme=path --cache.preimages bintrie convert --snapshot-out "$OUT/cli-anchor.pbt" --preimages-out "$OUT/cli-anchor.preimages" --memory-limit 256 --tmpdir "$TMP"
"$OUT/geth" --datadir "$DATA/cli-anchor" --state.scheme=path bintrie import --verify-only "$OUT/cli-anchor.pbt" "$OUT/cli-anchor.preimages" 0x35091d8791b6a548f2b95f4e8c0ad60efae033f4ccddbf58c60be1b36a107fce
```

Run collectors again into a separate output and compare all generated files byte-for-byte. Do not use `--force`, `--delete-source`, or an existing user DB. Initial collector used too little gas (200000): Amsterdam account creation charges 120×1530 state gas plus intrinsic cost. Corrected collector uses 1000000 gas and retains success assertions; failed outputs were not accepted.
