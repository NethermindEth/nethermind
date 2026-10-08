# Nethermind light client

This runnable project serves a bounded JSON-RPC surface from authenticated
finalized, optimistic and historical execution state without downloading the
full beacon state or execution chain. It connects directly to beacon libp2p
peers for light-client updates and to execution RLPx `eth/68`–`eth/72` plus
`snap/1` or `snap/2` peers for blocks, receipts, account, storage and bytecode
data. It verifies consensus signatures and Merkle branches, execution body and
receipt roots, SNAP ranges against the selected state root, and returned
bytecode against the authenticated code hash. No upstream JSON-RPC or Beacon
HTTP endpoint is used.

## Run

Install the SDK selected by the repository's `global.json`, then run from the
repository root:

```powershell
dotnet run --project src/Nethermind/Nethermind.LightClient -c release -p:SaveDiskSpace=true -- --network mainnet --checkpoint 0xYOUR-TRUSTED-FINALIZED-BEACON-BLOCK-ROOT
```

`--network` accepts `mainnet` (default), `hoodi` and `sepolia`. The listener defaults to
`http://127.0.0.1:8545`; `--urls` overrides it. `--help` prints the options.
The root must be a recent **finalized beacon block root**, obtained independently
from a source you trust. It is not an execution block hash or a beacon state
root. The client never obtains or replaces its trust anchor from its peers.
The bootstrap must be at most fourteen days old. That is a conservative local
policy, not a computation of the network's weak-subjectivity period.
`--data-dir` selects the verified consensus journal directory (default:
`src/Nethermind/artifacts/lightclient`). Restart with the same network and
checkpoint to replay saved updates; a new checkpoint starts a separate journal.
The saved files are verified again on replay and never replace the supplied
checkpoint.

The host listens for beacon P2P on TCP/UDP 9050 and execution P2P on TCP/UDP
30307. Outbound connections to public beacon and execution peers must be
permitted. The console prints live beacon, execution and SNAP peer counts every 12 seconds and logs each
local JSON-RPC request with its result and elapsed time. If no SNAP peer can
serve the selected state, state queries return an error after 30 seconds.

Example request:

```powershell
Invoke-RestMethod http://127.0.0.1:8545 -Method Post -ContentType application/json -Body '{"jsonrpc":"2.0","id":1,"method":"eth_getBalance","params":["0x0000000000000000000000000000000000000000","finalized"]}'
```

## Verified RPC contract

| Method | Result source |
| --- | --- |
| `eth_chainId` | Locally selected network |
| `eth_blockNumber` | Authenticated optimistic execution head, when available |
| `eth_getBalance` | Account RLP authenticated against the execution state root |
| `eth_getTransactionCount` | The same authenticated account's nonce |
| `eth_getStorageAt` | Requested storage key authenticated against that account's storage root; 32-byte DATA result |
| `eth_getCode` | Bytes whose Keccak hash matches that account's authenticated code hash |
| `eth_call` | Local Nethermind EVM execution over proof-backed selected state; no execution RPC |
| `eth_estimateGas` | Local Nethermind gas estimator over the same proof-backed selected state |
| `eth_getBlockByNumber`, `eth_getBlockByHash` | Authenticated execution header, transaction root, withdrawals root and block body |
| `eth_getBlockTransactionCountByNumber`, `eth_getBlockTransactionCountByHash` | Transaction count from the authenticated block body |
| `eth_getTransactionByBlockNumberAndIndex`, `eth_getTransactionByBlockHashAndIndex` | Transaction from the authenticated block body |
| `eth_getBlockReceipts`, `eth_getLogs` | Receipt trie and bloom authenticated by the block header; log results are complete within the accepted range |

State methods require an explicit `"finalized"` or `"latest"` selector,
an authenticated block number, or an EIP-1898 object naming its hash/number.
Historical state is available for up to 256 ancestors of the finalized head;
`pending` and `safe` are rejected. The optimistic head can be reorganized,
whereas the finalized head is stable. Each request captures a head once,
including during a concurrent consensus update. Returned account and storage values are not
trusted until their ranges reconstruct the selected state/storage root. Authenticated account/storage absence
returns zero or empty code; an incomplete proof fails.

`eth_call` and `eth_estimateGas` accept a transaction object and an explicit authenticated selector.
They fetch the selected execution header by its authenticated hash, then run
Nethermind's EVM against the corresponding state root. Accounts, storage slots
and bytecode are fetched from execution peers on demand and verified before a
clean execution attempt is resumed. A bounded 32 MiB cache makes repeated reads
fast; four calls may execute concurrently. Calls have a 10 million gas cap,
60-second deadline and at most 128 distinct state-fetch rounds. State changes
made by a call or estimate are discarded. `BLOCKHASH` uses the authenticated parent hash,
an anchored execution-header chain, or the verified EIP-2935 history-contract
storage as required by the active fork. Reverts return JSON-RPC error code `3`
with their return data. An estimate of a reverting transaction returns the same
error rather than a gas quantity. Blob/frame transactions, state overrides and
pending state are not supported.

Example `eth_call` reading WETH `totalSupply()`:

```powershell
Invoke-RestMethod http://127.0.0.1:8545 -Method Post -ContentType application/json -Body '{"jsonrpc":"2.0","id":1,"method":"eth_call","params":[{"to":"0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2","data":"0x18160ddd"},"finalized"]}'
```

JSON-RPC 2.0 batches (up to 32 requests) and notifications are supported. Transaction
submission and transaction-hash lookups return `-32601`: the execution peer
protocol does not provide a complete authenticated hash-to-block index.
No request is forwarded without verification. Receipt/log queries fail when
any block body or receipt set is unavailable; log ranges are capped at 16
finalized blocks and 10,000 matches.
Request bodies, peer responses and proof nodes have explicit size limits.
The listener admits at most 64 active requests and has no authentication; keep
it on loopback or place an authenticated gateway in front of it.
The console shows each JSON-RPC method, its result, and its handling time. Batch
items are logged individually; malformed requests and oversized bodies are
logged without their contents. Routine ASP.NET Core request logs are suppressed.

## Direct P2P transport

The beacon side uses
[light-client bootstrap, updates-by-range, finality and optimistic req/resp](https://github.com/ethereum/consensus-specs/blob/master/specs/altair/light-client/p2p-interface.md)
with SSZ-snappy framing and fork-context checks. The execution side uses
[SNAP account/storage ranges and bytecode](https://github.com/ethereum/devp2p/blob/master/caps/snap.md).
It runs Nethermind's discv4/discv5 discovery, RLPx, `eth/68`–`eth/72` and
`snap/1` or `snap/2` as the network schedule requires. Execution proofs are
cached in memory; it does no full block synchronization. Peers can withhold
recent selected state; this affects availability, never the returned
value. A recent trusted checkpoint remains required out of band.

## Security and current scope

The consensus processor follows the [Altair light-client update lifecycle](https://ethereum.github.io/consensus-specs/specs/altair/light-client/sync-protocol/)
with [Electra](https://ethereum.github.io/consensus-specs/specs/electra/light-client/sync-protocol/)
and [Gloas](https://ethereum.github.io/consensus-specs/specs/gloas/light-client/sync-protocol/) proof indices.
It verifies bootstrap root and committee membership, execution inclusion,
finality and next-committee branches, participant public-key subgroups, aggregate
BLS signature and signing domain, slot order and committee-period continuity.
It refuses committee conflicts and skips. Signed optimistic updates can move
`latest`; timeout recovery can advance the sync committee, but never promotes
an unfinalized execution state to finalized RPC.

The client implements Electra, Fulu and Gloas wire formats with built-in
mainnet, Hoodi and Sepolia schedules. Gloas authenticates an execution block
hash through the payload bid; the client then retrieves the matching execution
header by hash before serving its state. Gloas has synthetic fork-boundary
tests, but live Sepolia bootstrap was not obtained from connected peers during
the integration attempt, so Gloas interoperability is not yet demonstrated.
Pre-Electra is refused. Future network schedules must be maintained locally
rather than accepted from peers.

This has the checkpoint and sync-committee security assumptions of an Ethereum
light client, rather than independent validation of every execution block.
Peers can withhold data, but forged proof results fail verification.
Synchronization retries with bounded per-request deadlines and retains the last verified head on
failure. Finalized state reads fail once verified finality is over one hour old;
optimistic reads also require a recent signed head. Local time must be accurate.
The verified committee chain and latest finalized head are persisted with
atomic writes and revalidated from the original checkpoint on restart.
The in-memory execution proof cache is not persisted. Availability depends on a
SNAP peer retaining the selected state root.

## Browser and mobile work

The current desktop/server executable is not a browser or mobile package.
Verification must run on the device to preserve this trust model; calling a
remote instance moves verification trust to that server.

1. Extract a host-independent library containing the processor, transports and
   verified reads. Move the reusable beacon types, SSZ/domain helpers and network
   schedule out of the full `Nethermind.BeaconChain` dependency graph: it currently
   pulls in Init, Merge, Kestrel, libp2p and full-node dependencies. Minimize the
   trie/account dependencies as well. Keep the CLI as a separate thin host.
2. Port cryptography. The pinned `Nethermind.Crypto.Bls` package provides desktop
   native assets, including Linux ARM64, but no browser-WASM, iOS or Android
   assets. Build and validate a [WASM native BLS backend](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-native-dependencies?view=aspnetcore-10.0), plus iOS static and Android
   ARM64 native builds. Audit Keccak/intrinsic paths and native loading for each
   target; run the same valid/invalid vectors against every backend.
3. Browser: target .NET browser-WASM with a JavaScript/EIP-1193 provider bridge
   instead of a listening server, and run signature work in a Web Worker.
   Browsers cannot open the TCP/UDP sockets used by the current beacon and
   execution stacks. Direct P2P therefore needs browser-compatible transports
   (such as WebTransport/WebRTC plus compatible peers or relays) and a way to
   discover them without raw UDP. A browser HTTP gateway would be a different
   transport choice and must still supply data that the client verifies locally.
4. Mobile: embed the library in Android/iOS hosts (for example MAUI or a native
   application bridge), package the platform crypto libraries, and verify AOT,
   trimming and JSON metadata. Test suspend/resume, stale finality, clock changes,
   cancellation, background limits, battery cost and ARM64 memory use on devices.
   Native AOT has [platform-specific restrictions](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).
5. Adapt the existing verified consensus journal and bounded request admission
   to mobile lifecycle and storage APIs. Measure bundle
   size, peak memory and sync/signature latency before choosing deployment defaults.
6. Reuse the verification rules and test vectors across each platform and test
   actual peer interoperability. Keep the native host and transport separate
   from portable consensus and state verification code.

[Helios](https://github.com/a16z/helios) is a useful reference for the portable
library/host split. The present project does not provide those platform ports.

## Validation

```powershell
dotnet build src/Nethermind/Nethermind.LightClient.Test/Nethermind.LightClient.Test.csproj -c release -p:SaveDiskSpace=true -warnaserror
dotnet src/Nethermind/artifacts/bin/Nethermind.LightClient.Test/release/Nethermind.LightClient.Test.dll --progress off
```

Tests use synthetic signed committees, independently constructed Merkle/RLP
fixtures, offline JSON-RPC handlers, and synthetic SNAP ranges. They exercise
tampering, key-bound absence, committee rollover, fork/signature boundaries,
range verification, block selectors, cancellation and JSON-RPC envelopes.

On 2026-10-08 the beacon P2P transport connected to live mainnet peers,
authenticated a checkpoint and advanced finality from execution block 26145883
to 26147224. Execution P2P discovered current peers through the mainnet ENR tree,
negotiated SNAP, and returned `0x301f02576d753a` wei for
`eth_getBalance(0xde198901C5ee4611142E0c61DfbbCEc7ab468F32, finalized)`
from a locally verified account-range proof. That result is
0.013544893799298362 ETH at the verified finalized block; balances change over time.
