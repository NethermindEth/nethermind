# Captured Gnosis light-client proofs

These unchanged SSZ messages were captured on 2026-11-17 from the official
[`rpc-gbc.gnosischain.com`](https://rpc-gbc.gnosischain.com) Beacon API.
`expected.json` records the source paths, SHA-256 digests, JSON field values and
independent `/eth/v1/beacon/headers/{slot}` roots.

The bootstrap at slot 31178688 belongs to committee period 3805. The full update
authenticates its next committee, even though its attested header is older than
the bootstrap. The finality update at signature slot 31181113 belongs to period
3806 and verifies the committee rollover. The bootstrap is 2425 slots behind
that signature, within Gnosis's conservative 4096-slot recency bound.

`bootstrap.ssz` and `finality.ssz` are the complete `application/octet-stream`
responses. `update.ssz` is the complete message from the single updates response,
after removing its eight-byte length prefix and four-byte fork-digest context.
The original context is recorded in `expected.json`.

Network constants come from
[Gnosis config 2dd5746](https://github.com/gnosischain/configs/blob/2dd5746292ca298ae0695f1ee221098074574e04/mainnet/config.yaml)
and [Gnosis presets 045d46d](https://github.com/gnosischain/specs/tree/045d46d6db96a39b4d91485f9783474c13546ac9/consensus/preset/gnosis).
These tests run in the existing isolated Gnosis test process, using the actual
light-client verifier and journal. They require no network access.
