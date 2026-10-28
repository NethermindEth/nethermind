# Scheduled history observation regression

These deterministic requests exercise RX-PROVER-01 through the standalone v2
signer. They are constructed execution fixtures, not captures from a deployed
network. The six-block window starts at genesis. Its terminal Sync block contains
a scheduled history import followed by a signed transfer of 7 wei to SYSTEM_ADDRESS.
The allowed `historyUpdateOffset = 5`, with six 2-second slots per interval,
places the import at Sync. The default offset does not exercise this boundary.

The probe returns three ABI words: the system balance, the installed L1 block number,
and that block's hash. The expected outcomes are independently fixed:

| Request suffix | Claimed observation state | Expected result |
| --- | --- | --- |
| `before-history` | Balance 0, empty public history `(0, 0)` | Reject before signing |
| `before-ordinary` | Balance 0, newly imported L1 header | Reject before signing |
| `correct` | Balance 7, newly imported L1 header | Sign the committed digest |

Each case is supplied as both a static call and a successful mutable lookup
whose source frame reverted. Both pin the full terminal/ordinary block hash.
The native blob and settlement ABI agree on the claimed outcome, including the
invalid cases; only independent observation replay distinguishes them.

`*.v2.protobuf` contains length-delimited `ClientFrame` messages for a cold
`Begin -> Block* -> Finalize` request. The client binds each frame to the session
and epoch issued by its server. `identity.json` pins the exact request Keccak,
public-input digest, deployment inputs and independently configured validation
identity. Digests are computed from inputs, never learned from server responses.
The signing key is the intentionally public Anvil account #1 key.
The version-7 network-bound key authenticates the local execution configuration;
the signed anchor uses the original Core Rollup's `(timestamp, blockHash)` context.

The generator executes blocks to construct witnesses and roots, then fixes the
expected probe values independently. This is a regression oracle for replay
position, not an independent EVM implementation. A separate backend regression
also covers a native EEZL2 suffix after the ordinary prefix.

The ordinary unit test reconstructs and compares every fixture byte. Explicit
regeneration is available after reviewing intentional changes:

```bash
cargo +1.95.0 test --locked -p eez-proof-signer -- --ignored --exact \
  service::tests::derivation::history_observations::regenerate_history_observation_process_fixtures
cargo +1.95.0 test --locked -p eez-proof-signer -- history_observations
cargo +1.95.0 test --locked -p eez-node-e2e --test node_e2e -- \
  cross_chain::history_observations:: --test-threads=1
```

The process test starts a cold signer with outbound networking denied by the
existing seccomp launcher. It requires six block acknowledgements per case,
four `observation_replay` rejections with no signature, and two signatures over
the expected digests. No L1/L2 RPC, node database, Anvil chain or contract
deployment supplies authority to this test.

The ordinary transaction donates seven wei to SYSTEM_ADDRESS. The observation
probe reads that in-block balance; finalization burns it in the closed block.
Consequently the zero-balance/current-origin case also rejects a result obtained
from the closed state rather than the selected in-block transaction boundary.
Both static and mutable lookups must reach the signing gate only with the
seven-wei result, including with a following native group in signer unit tests.
