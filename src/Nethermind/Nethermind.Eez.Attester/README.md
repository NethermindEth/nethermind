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
