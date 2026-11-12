# Native offline bidirectional settlement fixture

Captured from `cross_chain::minimal_bidirectional_cross_chain_smoke` with
`EEZ_TEST_PROOF_SIGNER_NO_NETWORK=1`. The source-built signer ran under the
outbound-network-denying launcher, and the native manager accepted its signature.
The process test independently checked the public input and canonical L2 safe hash.
The captured execution includes recorded native history transactions and their
metered gas with history 0x46/version 1. Cross-chain transactions use
versionless 0x45 with identities bound to the L2 EVM chain ID, intended L1
parent and zero-based complete-group ordinal in the full Live Sync block. Complete cross-chain groups clear
SYSTEM/EEZL2 residue before their checkpoints; ordinary fees reach FeeVault.

`request.v2.protobuf` is the complete length-delimited `prove.v2.ClientFrame`
sequence reconstructed by the test proxy. It contains all L2 witnesses and L1
ancestry. `response.json` fixes the expected signature, digest, byte count and request
Keccak. The local genesis documents and public Anvil deployment identities are
required for cold independent replay. No RPC endpoint, operator secret or cache is
part of this fixture.

Source details and the executed command are recorded in
[the implementation audit](../../../../../docs/audits/2026-10-27-upgradeable-l1-history/README.md).

This capture includes the fixed FeeVault predeploy, its initial owner/recipient,
the single `Profile` commitment and the original validation-contract version 6. The sole
prover service remains `prove.v2.Prover`; its protobuf messages are unchanged.

The history predeploy is an initially empty EEZProxy with a separate ProxyAdmin.
Native history updates carry explicit `(number, blockHash)` pairs. The public
store remains zero until its first system import.

## Core Rollup proof-policy adaptation

The recorded request, execution witnesses, L1/L2 genesis and transaction bytes
are retained unchanged. `identity.json` now records the version-7 network-bound
verification key; `response.json` contains the digest and ECDSA signature
recomputed from those inputs using Core's `(timestamp, blockHash)` context.
These updated signatures are derived fixture expectations, not new deployed-network
captures. The signer replay tests must accept them after complete independent
execution; regeneration alone does not establish that validation.

Regenerate only through the explicit
`service::tests::derivation::captured::regenerate_native_fixture_commitments`
utility. It also emits local Core-verifier oracle inputs under
`artifacts/rollup-core-fixture-oracle/` for an independent Solidity comparison.
