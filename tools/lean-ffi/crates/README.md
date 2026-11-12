# Vendored leanVM crates

`rec_aggregation` and `signatures` derive from [leanEthereum/leanVM](https://github.com/leanEthereum/leanVM) at
`dd759d2cc5f59d38fb5e118346791e837733065c`, the revision `../Cargo.toml` pins for every other leanVM crate. Upstream
licenses them under MIT or Apache-2.0 (`LICENSE-MIT`, `LICENSE-APACHE`). The core (`leanvm_core`, `verifier`,
`lean_compiler`, `fiat_shamir`, `pcs`, `primitives`) stays an unmodified git dependency.

Changes against upstream, all EIP-8288 specific:

- `signatures::lean_sphincs`: NiceTry v2 (SPHINCS- v2) parameters and encoding, the wallet's signature scheme. The vendored
  copy drops leanXMSS; test-only mock signers come from the upstream crate.
- `rec_aggregation`: the aggregation guest (`guests/lean_ethereum.py`) commits to the EIP-8288 dependency digest with
  Keccak-256, verifies NiceTry v2 signatures (scheme `0x10`) and leanVM execution proofs of programs up to 2^11
  instructions (scheme `0x11`, `src/aggregation/generic.rs`), and merges or discards child aggregates.

Every change to a guest changes `AGGREGATED_VK`; `../examples/fixtures.rs` prints it and the native tests check it
against `Eip8288Constants.AggregatedVk`.
