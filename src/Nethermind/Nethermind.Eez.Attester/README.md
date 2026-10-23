# EEZ attester streaming endpoint

The existing prove.v1 listener is the default. Enable prove.v2 with
`--streaming-config=/path/to/streaming.json` (or `EEZ_STREAMING_CONFIG`).
All other attester deployment/key/chain flags still apply.

```json
{
  "profile": "rollup0-calldata",
  "listenAddress": "0.0.0.0:50063",
  "l1Rpc": "http://127.0.0.1:8545",
  "l2Rpc": "http://127.0.0.1:8546",
  "l1ChainId": 31337,
  "registry": "0x0000000000000000000000000000000000000001",
  "manager": "0x0000000000000000000000000000000000000002",
  "serverCertificate": "server.pfx",
  "serverPasswordFile": "server-password.txt",
  "composerCertificates": ["composer.cer"],
  "maxConnections": 8,
  "maxSessions": 8,
  "maxSessionsPerOwner": 2,
  "maxSessionBytes": 67108864,
  "maxReservedBytes": 536870912,
  "retentionSeconds": 600
}
```

Replace the example chain ID, contract addresses and source URLs with your deployment.
Certificate/password paths are relative to the configuration file. Use a server PFX
with its own private key and a password file. Composer pins are public PEM/DER
certificates. The TLS handshake requires the matching client private key. Certificate
pins and validity are checked for each authenticated connection/request. Changing pins
requires a restart; retained sessions belong to the exact SHA256 certificate identity.

When v2 is enabled, keep `--listen-addr` on loopback. The v1 method cannot be called on
the TLS listener. v1 and v2 share one backend execution slot. Configure both block/byte
limits and total retained-byte budgets for the available memory. Each retained session
reserves four times its byte limit for retained and temporary payload copies, so the example budget admits at most two full sessions. This reservation does not include all ABI decoding, protobuf object or EVM working memory.

L1/L2 RPC sources are trusted operator infrastructure. L1 must serve `eth_chainId`,
`eth_getBlockByNumber` and canonical EIP-1898 `eth_call`. L2 must serve `eth_chainId`
and `debug_getRawHeader` for selected predecessors. Evidence calls have a five-second
deadline and finite response-size limits. RPC redirects are disabled. No source URL,
registry address, key or signer identity can be selected through streamed frames.

This profile supports timeless calldata Rollup0 settlement only. Finalize requires
strict proofless PostBatch ABI calldata. Blobs, posting context and other profiles are
refused before signing. This endpoint does not imply RollupX or nonzero synchronous
delivery support. No performance claim or remote deployment is included.

For a Rollup0 chain that omits L1 consensus transitions, set
`--skip-consensus-layer-calls=true` or `EEZ_SKIP_CONSENSUS_LAYER_CALLS=true`.
The matching node setting is `Eez.SkipConsensusLayerCalls=true`. Select the same
mode on every follower and prover before starting the chain. This skips
EIP-4788 and EIP-6110/7002/7251 calls and retains EIP-2935. The default is false
for compatibility with existing deployments and recorded execution profiles.
It does not define an activation block for an existing chain. RollupX already
omits these calls.

The optional `rollup0-conditional` streaming profile validates a transition from
its selected parent without an independent L1 acceptance lookup. Select it
explicitly in the trusted streaming configuration. Omit `l1Rpc`, `l2Rpc`,
`l1ChainId`, `registry` and `manager`; unused authorization settings are rejected.
Mutual TLS, pinned composer identity, quotas, canonical decoding, full execution
and settlement validation, and session-owned signing still apply.

This profile accepts inherited proof arrays as bounded input data, as the
published Rollup0 composer does. Proof bytes are outside the signed public-input
digest and are never trusted as evidence. Every result is freshly signed after
validation. This differs from the common spec's proofless-input wording. The
`rollup0-calldata` and `rollupx-native` profiles keep their existing proofless and
independent L1 authorization rules.

The `rollupx-native` profile also serves native inline-window `prove.v1` on
the loopback listener. It requires physical blobs, an L1 posting context,
canonical parent provenance and the exact local validation identity returned
by `Capabilities`. Legacy Rollup0 hash claims and cached range references are
rejected. Both wire versions use the same native execution, DA, settlement,
authorization and final canonical recheck before fresh signing.

`Capabilities` advertises zero preparation/cache capacity. `PrepareOrdinary`
and `RetainOrdinaryRanges` return `Unimplemented`. The validation identity binds
our validator modules, trusted chain documents and deployment in our own
namespace. It is not the upstream implementation's cache fingerprint.
The published native composer currently requires nonzero preparation/cache
capacity and its own implementation-specific identity, including its lockfile.
This inline support does not establish compatibility with that composer or
live L1 acceptance of proofs from our native prover. Those remain separate gates.

The `rollupx-native` profile selects its streaming wire rules at startup. It
retains the published RollupX status and bound-control response behavior while
Rollup0 retains its existing behavior. A bound cursor is returned only for the
exact current session and epoch. Retired or foreign attachments receive no
cursor.

RollupX byte quotas charge the complete accepted Begin and Block protobuf
frames plus the candidate Finalize frame. Duplicate validated blocks are not
charged twice. A rejected Finalize preserves the retained prefix and its byte
charges. Rollup0 keeps separate retained-block and settlement budgets.

At capacity, RollupX and conditional Rollup0 may evict the same owner's oldest detached, idle session.
Active attachments and draining workers keep their reservations. A resumed
attachment cannot be detached by cleanup from an older stream. Completed proof
streams retain ownership through terminal delivery and release it on cleanup.

Malformed physical inputs remain `InvalidArgument`. Canonical calldata with
inconsistent configured settlement claims returns `FailedPrecondition`.
RollupX uses `OutOfRange` only for the known gRPC receive-size error; other
capacity errors remain `ResourceExhausted`.

The 29 upstream RollupX recordings explicitly use synthetic L1 authority.
Their replay uses a finalizer in the test assembly that selects predecessors
from the immutable baseline fixture. It calls the same unsigned native execution,
DA, settlement and observation verifier as the production finalizer, then signs
through the session fence. Original requests, expected frames and proof oracles
remain unchanged. No production option selects this fixture authority.

Separate production endpoint tests retain full-window positive proofs and reject
incomplete Sync intervals, missing non-genesis parent acceptance, changed
settlement claims and canonical reorgs. Synthetic transcript conformance does not
establish canonical L1 proof acceptance or live RollupX interoperability.
