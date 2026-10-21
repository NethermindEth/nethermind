# Captured Fulu REST responses

Captured on 2026-10-08 from the public beacon API at `https://ethereum-beacon-api.publicnode.com/`:

- `finality.json`: [`/eth/v1/beacon/light_client/finality_update`](https://ethereum-beacon-api.publicnode.com/eth/v1/beacon/light_client/finality_update), attested slot 15384813, finalized slot 15384736, signature slot 15384814.
- `bootstrap.json`: [`/eth/v1/beacon/light_client/bootstrap/0x4fda428c7ca70ced8ecd846de2a37a339f0518260f848dc5d5d42a4e47b7b3ba`](https://ethereum-beacon-api.publicnode.com/eth/v1/beacon/light_client/bootstrap/0x4fda428c7ca70ced8ecd846de2a37a339f0518260f848dc5d5d42a4e47b7b3ba).

The checkpoint is pinned to the SSZ root of the captured finalized beacon header. The offline fixture test maps these REST response bodies to the light-client containers, round-trips them through the production SSZ wire codec, and checks the bootstrap proof, execution-header proofs, finality proof, and BLS signature in the light-client store. The runtime uses beacon P2P SSZ responses, not REST JSON.

This endpoint-derived checkpoint is an interoperability fixture anchor only. It does not establish a trustworthy runtime checkpoint; users must independently obtain their own recent trusted beacon checkpoint.

The `Hoodi/` responses were captured on 2026-10-12 from
`https://ethereum-hoodi-beacon-api.publicnode.com/` using the same routes.
The finalized beacon header root is pinned to
`0x5d5f221a04833992d266353bebe3d515dcd7ca9d4afa5550733f978eac535f51`:
finalized slot 4130560, attested slot 4130643, signature slot 4130644.

Both captures are checked offline for authentic committee, execution and
finality proofs and real BLS signatures. Additional cases reject changed
bootstrap proofs, a different anchor, a tampered signature, a different signing
network and a changed finality proof without publishing invalid state. Journal
replay must preserve the authenticated optimistic and finalized heads and the
participation maximum. The checked-in responses have no live-network test
dependency; their original freshness is irrelevant to these historical fixtures.
