# Electra reference Merkle proofs

These are Ethereum consensus-spec-tests mainnet Electra `single_merkle_proof/BeaconState` fixtures pinned to [`bc5c1a7fb2a8871aaffd4b16ee4dd9c72bb81908`](https://github.com/ethereum/consensus-spec-tests/tree/bc5c1a7fb2a8871aaffd4b16ee4dd9c72bb81908/tests/mainnet/electra/light_client/single_merkle_proof/BeaconState).

`BeaconState.ssz_snappy` is the unchanged `current_sync_committee_merkle_proof/object.ssz_snappy` download. The three JSON files preserve the upstream `proof.yaml` leaf, generalized index, and branch, with a source URL, so these tests do not need a YAML parser. All three proofs describe the same state. The test independently decodes and hashes that state before checking each proof and a corrupted sibling.

These fixtures cover the current committee (86), next committee (87), and finalized root (169) proofs. They do not represent a complete light client synchronization test suite. Upstream synchronization fixtures use the minimal preset, whose committee and epoch dimensions differ from this project's Ethereum mainnet preset.
