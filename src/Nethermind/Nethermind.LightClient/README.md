# Nethermind light client

This runnable project serves a bounded JSON-RPC surface from authenticated
finalized, optimistic and historical execution state without downloading the
full beacon state or execution chain. It connects directly to beacon libp2p
peers for light-client updates and to execution RLPx `eth/68`–`eth/72` plus
`snap/1` or `snap/2` peers for blocks, receipts, account, storage and bytecode
data. It verifies consensus signatures and Merkle branches, execution body and
receipt roots, SNAP ranges against the selected state root, and returned
bytecode against the authenticated code hash. Beacon HTTP is used only to obtain
the startup trust anchor when a checkpoint URL is selected; all subsequent
consensus and execution data come from P2P peers, never an upstream JSON-RPC endpoint.

## Run

Install the SDK selected by the repository's `global.json`, then run from the
repository root:

```powershell
dotnet run --project src/Nethermind/Nethermind.LightClient -c release -p:SaveDiskSpace=true -- --network mainnet --checkpoint 0xYOUR-TRUSTED-FINALIZED-BEACON-BLOCK-ROOT
```

Hoodi and Sepolia can start without `--checkpoint`; they fetch a finalized
checkpoint from the [EF ethPandaOps checkpoint services](https://eth-clients.github.io/checkpoint-sync-endpoints/).
There is no public EF-operated mainnet checkpoint endpoint: the [EF mainnet Beacon API](https://ethpandaops.io/docs/guides/client-developers/api-access/)
requires credentials. For mainnet, provide a trusted root as above or select a
trusted HTTPS provider explicitly, for example:

```powershell
dotnet run --project src/Nethermind/Nethermind.LightClient -c release -p:SaveDiskSpace=true -- --network mainnet --checkpoint-url https://mainnet.checkpoint.sigp.io
```

The `Nethermind.LightClient` debug profile selects mainnet and supplies the
checkpoint URL above automatically. Edit its command-line arguments to select
another trusted provider or root.
Select `Nethermind.LightClient (Sepolia)` or `Nethermind.LightClient (Hoodi)`
from Visual Studio's launch-profile dropdown to debug those networks using
their EF-operated checkpoint defaults. All profiles use RPC port 8545.

Press **Ctrl+C** in the terminal to cancel checkpoint/bootstrap startup or stop
the running client. Shutdown waits for RPC and sync workers, closes P2P
connections and storage, then flushes logging.
Native P2P bootstrap retries until a verified response arrives or shutdown
cancels the operation. Individual peer requests retain bounded timeouts.

The example mainnet URL is operated by Sigma Prime, not the EF. The client
fetches its finalized checkpoint through the standard
[`finality_checkpoints` Beacon API](https://github.com/ethereum/beacon-APIs/blob/master/apis/beacon/states/finality_checkpoints.yaml)
over HTTPS with a 10-second timeout and a 4 KiB response limit. It does not
follow redirects. `--checkpoint` takes precedence over `--checkpoint-url` and
avoids the primary provider HTTP requests. Add `--checkpoint-check-url
https://ANOTHER-BEACON-API` to require a separately chosen provider to report
the selected root as canonical and finalized on the same network. For a
provider-selected root, the cross-check must use a different host. This is
another provider assertion: P2P signatures authenticate updates *after* that
root, but do not independently establish whether it belongs to the canonical
chain. Provider network genesis root and time are checked before initial
selection and before a cross-check.

`--network` accepts `mainnet` (default), `hoodi` and `sepolia`. The listener defaults to
`http://127.0.0.1:8545`; `--urls` overrides it. `--help` prints the options.
`--log-level Debug` adds received beacon-update slots, validation outcomes and
durable consensus progress to the light-client logs; the default is `Info`.
Debug also records selected peer admission, advertised capabilities, connection
outcomes and disconnect reasons from the consensus and execution transports.
To use MetaMask or another browser wallet extension, select
`http://127.0.0.1:8545` as the RPC URL for Ethereum
Mainnet (chain ID `1`), or Sepolia (chain ID `11155111`). In MetaMask's network
settings, edit the existing network and add the local endpoint to its RPC URLs;
see the [wallet setup guide](https://support.metamask.io/configure/networks/how-to-add-a-custom-network-rpc#adding-or-editing-rpc-urls).
HTTP RPC and WebSocket subscriptions share the port; the WebSocket URL is
`ws://127.0.0.1:8545/ws`.

The HTTP listener starts before checkpoint selection, beacon bootstrap and
execution discovery. `GET /status` reports the startup stage (`checkpoint`,
`beacon`, `bootstrap`, `execution`, then `running`), whether RPC initialization
has completed, authenticated latest/finalized heights and freshness, each hosted
account's retained snapshot and refresh outcome, and authenticated history
coverage/backfill progress. Unknown authenticated heads are `null`; listening
or `running` alone does not mean wallet data is ready. The `latest.fresh` policy
is 120 seconds and the `finalized.fresh` policy is one hour. A hosted account's
`current` flag means its complete retained state matches the reported latest
state root; check latest freshness separately. History coverage is independent
of these flags and grows on demand.

During initialization, `eth_chainId` is available and `eth_syncing` returns the
standard `startingBlock`, `currentBlock`, `highestBlock` quantity object. Its
zero heights mean execution progress is unknown; `/status` supplies the startup
stage. Other HTTP reads wait for RPC initialization within the original
90-second request deadline and observe caller disconnect and application shutdown.
Transaction submission invoked before initialization returns an actionable startup
error. WebSocket upgrades return HTTP 503 until RPC
initialization completes. After initialization, `eth_syncing` follows the
authenticated latest head; it does not promise all configured accounts or
requested history have finished downloading.

Loopback listeners reject non-loopback Host headers. Requests from browser
extensions (`chrome-extension://` or `moz-extension://`) are allowed, including
CORS preflights. CORS does not enable credentialed cross-origin requests. Ordinary
website and opaque (`null`) origins are rejected on both HTTP and WebSocket
connections. This permits installed wallet extensions; it does not authenticate
an extension or caller. Public listeners require an authenticated gateway.
The root must be a recent **finalized beacon block root**. It is not an execution
block hash or a beacon state root. The client never obtains or replaces its
trust anchor from its peers.
The bootstrap, and on restart the latest replayed authenticated header, must be
at most 256 epochs of slots old (27 hours 18 minutes 24 seconds on these
networks). This uses the minimum validator withdrawal delay, a conservative
lower bound of the supported forks' [weak-subjectivity periods](https://ethereum.github.io/consensus-specs/specs/gloas/weak-subjectivity/).
It does not compute the full state-dependent validator churn term. A saved
original checkpoint may be older when replay proves recent authenticated
progress; a stale replay requires an explicitly chosen recent checkpoint.
`--data-dir` selects the consensus journal and networking storage directory (default:
`src/Nethermind/artifacts/lightclient`). Restart with the same network and
checkpoint to replay saved updates; a new checkpoint starts a separate journal.
The journal files are verified again on replay and never replace the chosen
checkpoint. A provider-selected root, its epoch, source URL and selection time
are persisted in `<network>-checkpoint.json` and reused on restart without
fetching a replacement from that provider. Changing the provider requires an
explicit `--checkpoint` or a separate `--data-dir`. Journals from earlier
versions without provider provenance require their original explicit root;
they cannot be abandoned implicitly in favor of a fresh provider root.
The journal retains the committee chain, finalized and optimistic heads, and
a separately bounded timeout-recovery candidate. The candidate, including an
explicit empty state after finality, is bound to the exact saved chain and head
generations. Its signature and branches are reverified on restart without
changing the replayed heads or participation thresholds. Interrupted or corrupt
candidate persistence disables that recovery candidate while preserving the
authenticated replay state.
Optimistic polling runs independently of committee catch-up and finality polling,
once per slot and when an RPC is waiting for a fresh head, so a delayed repair
request does not postpone optimistic recovery. Up to three suppliers are queried
concurrently; consensus updates still pass through the same serialized
verification and persistence path. Beacon handshakes advertise the authenticated
head and finalized checkpoint after durable publication; this does not claim full
execution validation or retained sidecars. Execution discovery starts alongside beacon
bootstrap. The client targets 16 beacon peers (maximum 32) and allows 32 execution
peers, replenishing SNAP capacity when fewer than eight usable suppliers remain.

Native beacon networking keeps its private P2P identity and monotonically
increasing signed ENR sequence in `<data-dir>/cl/<chain-id>/network`, using
Nethermind's RocksDB storage with synchronous WAL writes before advertisement.
Restart with the same data directory to retain the public PeerID; this identity
is separate from wallet keys and is isolated per network. A repaired database
stops startup rather than silently reusing an identity with an older sequence.
Recovery must preserve the last published ENR sequence. If that cannot be
established, remove the whole networking directory to create a new identity.

Signed beacon peer ENRs are cached separately in
`<data-dir>/cl/<chain-id>/discovery`, with at most 512 records and a 2 MiB file
limit. The client snapshots discovery every 30 seconds and at shutdown. On
restart it rechecks signatures, fork compatibility and usable endpoints, then
probes saved peers alongside configured bootnodes; only responsive records enter
normal dialing. The live authenticated handshake is still required: this cache
grants no trust to a peer. An unavailable
or invalid cache falls back to fresh discovery. Peer bans, connection state and
execution proof caches are not persisted.

Authenticated outbound suppliers leave at most 32 dial hints in the networking
store, expiring after 24 hours. Restarting with the same data directory retries
these addresses through normal admission and backoff checks. Supplier preference
and head progress use newly authenticated data from the current session.

The host listens for beacon libp2p on TCP 9050, beacon discv5 on UDP 9050,
beacon QUIC v1 on UDP 9051, and execution P2P on TCP/UDP 30307.
Set `--beacon-port`, `--beacon-quic-port` and `--execution-port` to distinct
ports to run multiple clients on the same host. The beacon port selects both
TCP and discv5 UDP; the execution port selects both TCP and discovery UDP.
Set `--beacon-quic-port 0` to disable QUIC.

QUIC uses mutually authenticated TLS 1.3 and its own stream multiplexing. Signed beacon
ENRs advertise the separate QUIC port; discovered QUIC addresses are tried
before TCP, with TCP fallback when QUIC cannot connect. All libp2p components
use version 1.1.0.
Outbound connections to public beacon and execution peers must be
permitted. The console prints live beacon, execution and SNAP peer counts every 12 seconds and logs each
local JSON-RPC request with its result and elapsed time. If no SNAP peer can
serve the selected state, state queries return an error after 30 seconds.
Execution data requests race at most two suppliers, use only a verified response,
and cancel and join the other attempt. Account, storage and bytecode reads have
separate 32 MiB verified caches shared by RPC, EVM and hosted-account refreshes.
Authenticated headers have another 32 MiB cache; each read rechecks the requested
number and state root and returns an independent mutable copy.
Concurrent misses share one download; canceling one caller does not cancel the
other callers. Cache keys bind accounts to the world-state root, storage to its
authenticated storage root and slot, and bytecode to its code hash.
Both P2P stacks use Nethermind's automatic IPv4/IPv6 IP resolver and its bounded
retry, refresh and address-validation behavior. The resolved public address is
used in discovery records; IP detection does not open or forward router/firewall
ports. Failure to resolve an address leaves outbound discovery and dialing available.

QUIC requires the platform's [MsQuic runtime](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview#platform-dependencies).
Windows 11/Server 2022 or later includes it with .NET. Linux requires
`libmsquic` 2.2 or later; for example, `sudo apt-get install libmsquic` after
configuring the appropriate package repository. macOS support is partial and
requires installing `libmsquic` and configuring its library search path.
If the runtime does not support QUIC, the light client logs a warning and uses
TCP without advertising QUIC. The embedded full consensus plugin keeps QUIC
disabled by default; set `BeaconChain.QuicPort` to a distinct UDP port to enable it.

Example request:

```powershell
Invoke-RestMethod http://127.0.0.1:8545 -Method Post -ContentType application/json -Body '{"jsonrpc":"2.0","id":1,"method":"eth_getBalance","params":["0x0000000000000000000000000000000000000000","latest"]}'
```

## Host selected accounts

Add `--host-accounts` to the run command to retain complete state for specific
accounts alongside the normal bounded proof cache:

```powershell
dotnet run --project src/Nethermind/Nethermind.LightClient -c release -p:SaveDiskSpace=true -- --network mainnet --checkpoint-url https://mainnet.checkpoint.sigp.io --host-accounts 0xde198901C5ee4611142E0c61DfbbCEc7ab468F32,0x50614cc8e44f7814549c223aa31db9296e58057c
```

The comma-separated list selects accounts explicitly; duplicate addresses are
ignored. Each snapshot includes balance, nonce, bytecode and **all nonzero storage
slots**, rather than only slots previously requested by RPC. SNAP range proofs
authenticate every page, and the complete retained storage must reconstruct the
proven storage root before publication. Missing slots then have a verified zero
value. Full storage includes mapping entries without needing their original keys.

Snapshots stay in RAM without cache eviction. Only the latest completed snapshot
of each configured account is retained; old versions are replaced. This is not
disk persistence: restart downloads the selected state again. Large contracts
can require substantial RAM, temporary verification memory, bandwidth and time;
there is deliberately no size eviction for accounts you select. No accounts are
hosted by default.

Independent account workers follow authenticated optimistic `latest`, waking on
new authenticated heads with a one-second retry fallback. Up to four accounts
refresh concurrently, so a slow account does not block the others. Heads that
arrive during a download are coalesced; the next refresh selects the newest fresh
head. At a new root each worker fetches
a fresh account proof. Unchanged storage roots and code hashes reuse their
verified data; changed storage uses BAL updates automatically from the network's
EIP-7928 activation, with complete SNAP downloads before activation or as fallback.
A snapshot becomes visible atomically only when all its data has verified.
Reorganizations use the same root checks. While catching up, RPC uses the regular proof-backed P2P path
for anything the retained snapshot cannot authenticate at the requested root.
Other accounts and historical roots continue to use that path. `eth_getProof`
continues to request witnesses from peers. Shutdown cancels and joins all workers.

Hosted storage uses [EIP-7928 BALs](https://eips.ethereum.org/EIPS/eip-7928)
automatically from the activation block onward, using each authenticated block's
release spec. There is no separate BAL option; `--host-accounts` selects the
accounts to retain. The first snapshot still requires a complete SNAP download.
Balance, nonce and code commitments continue to come from fresh account proofs;
changed bytecode is fetched and hash-checked normally, including hash-only code
adoption.

Both SNAP protocol versions are offered from startup, so starting before
activation does not require a restart to use BALs afterward. BAL requests remain
gated by the authenticated block's fork; available peers must support SNAP/2 or
ETH/71+.

The updater verifies every intervening execution header and its ancestry back to
the retained snapshot before fetching any BALs. It obtains BALs over SNAP/2 or
ETH/71+, checks their raw RLP hashes against the authenticated headers, applies
each slot's final value in block order and removes zeroed slots. The resulting
complete local storage must reconstruct the freshly proven account storage root
before publication. It still rebuilds that local trie; BALs reduce network
transfers rather than eliminating work proportional to hosted storage size.

Catch-up is limited to 256 forward blocks, 30 seconds per account attempt and
16 MiB per encoded BAL. Only the latest fetched BAL is retained for reuse across
accounts. Unsupported forks or peers, reorgs, larger gaps, timeouts, missing or
invalid data and root mismatches fall back to the complete SNAP download. If
that also fails, the previous verified snapshot stays available at its original
root. Merely relabeling old state with a new root is never safe.

## Verified RPC contract

| Method | Result source |
| --- | --- |
| `eth_chainId` | Locally selected network |
| `eth_blockNumber` | Last authenticated optimistic execution height, including while synchronizing |
| `eth_syncing` | Consensus freshness: `false` for a fresh authenticated optimistic head; otherwise standard progress quantities |
| `eth_getBalance` | Account RLP authenticated against the execution state root |
| `eth_getTransactionCount` | The same authenticated account's nonce |
| `eth_getStorageAt` | Requested storage key authenticated against that account's storage root; 32-byte DATA result |
| `eth_getCode` | Bytes whose Keccak hash matches that account's authenticated code hash |
| `eth_getProof` | Standard [EIP-1186](https://eips.ethereum.org/EIPS/eip-1186) account and storage path witnesses, reconstructed from verified SNAP ranges and verified again before export |
| `eth_call` | Local Nethermind EVM execution over proof-backed selected state; no execution RPC |
| `eth_estimateGas` | Local Nethermind gas estimator over the same proof-backed selected state |
| `eth_getBlockByNumber`, `eth_getBlockByHash` | Authenticated execution header, transaction root, withdrawals root and block body |
| `eth_getBlockTransactionCountByNumber`, `eth_getBlockTransactionCountByHash` | Transaction count from the authenticated block body |
| `eth_getTransactionByBlockNumberAndIndex`, `eth_getTransactionByBlockHashAndIndex` | Transaction from the authenticated block body |
| `eth_getTransactionByHash`, `eth_getTransactionReceipt` | Bounded observed transaction index and recent authenticated block scan; body and receipt roots are verified locally |
| `eth_getBlockReceipts`, `eth_getLogs` | Receipt trie and bloom authenticated by the block header; log results are complete within the accepted range |
| `eth_feeHistory` | Authenticated headers and complete verified receipts for gas-weighted effective tips |
| `eth_gasPrice`, `eth_maxPriorityFeePerGas` | Local fee suggestion from the authenticated latest block; gas price adds the derived next-block base fee |
| `eth_sendRawTransaction` | Local envelope, signature, fork and authenticated state checks, followed by execution P2P relay; blob sidecars are KZG verified |

State methods accept an explicit `"finalized"` or `"latest"` selector,
an authenticated block number, or an EIP-1898 object naming its hash/number.
Wallets and dapps can use `"latest"` for the recent sync-committee-authenticated
head. Gossip advances it between recovery polls; it is not restricted to the
older finalized block. Use `"finalized"` explicitly when finality is required.
`"latest"` state, block, log, fee and transaction-preparation requests require
an authenticated head within ten beacon slots (120 seconds on supported networks)
of the current slot. While it is unavailable or stale, these requests trigger
coalesced recovery and wait within the existing RPC deadline; stale data is never
served as `latest`. Explicit block
numbers and hashes are outside these time-based freshness gates and retain their
separate authenticated-history bounds. Explicit `"finalized"` selectors require
finality within one hour; an older authenticated finalized anchor can still
authenticate explicit historical selectors and ancestry.
`eth_blockNumber` reports the last known height and is not a freshness check.
`eth_syncing` takes no parameters and reports `startingBlock`, `currentBlock`,
and `highestBlock` as hex quantities while waiting for a fresh head. The starting
height is fixed when the RPC is created; the highest height is the known
verified tip, so it can equal the current height when the remote target is unknown.
This status is available before checkpoint bootstrap; unknown execution heights
are initially zero.
A `false` result describes consensus freshness, not hosted-cache completeness or
historical log coverage; requested data still has to be fetched and verified.
Historical state selectors are limited to 32 ancestors of either the finalized
head or the authenticated optimistic head. A wallet's pinned prior
optimistic hash remains usable when the native head advances, provided it is
still an ancestor in that window; orphan hashes fail canonical validation.
availability within that range still depends on a SNAP peer retaining the state root.
At most two historical state requests run concurrently; excess requests fail promptly,
leaving SNAP and HTTP capacity for current-head reads.
Block and receipt selectors retain a separate 256-ancestor window anchored to
either head. Log queries accept at most 512 inclusive blocks, including ranges
crossing finality on one pinned authenticated chain. Omitted bounds select
authenticated `"latest"`; crossing finality verifies the finalized block hash
on the selected chain. Optimistic ancestry is transient and bounded to 512
parent steps behind the pinned optimistic head. Older finalized ranges and log
`blockHash` selectors use an on-demand authenticated header cache retaining at
most 262,144 headers, including the finalized anchor. Each bloom candidate's
complete receipts are verified before exact address and topic filtering.
Candidates without matching logs finish from verified receipts; matching blocks
also fetch and verify their bodies for transaction hashes and coordinates.
Cached headers alone do not establish complete log results.
Verified bodies and receipts have separate 32 MiB weighted in-memory caches keyed
by block hash. Concurrent misses for the same payload share one download; canceling
a caller does not cancel other callers' download. Log queries prefetch up to four
bloom-positive blocks while preserving canonical result order. Bodies, receipts
and BAL requests share at most three of the four execution request slots, leaving
capacity for headers; SNAP state requests use a separate request budget.
Complete finalized numeric-range log results have a separate 32 MiB weighted
in-memory cache, including verified empty results. Entries bind the normalized
filter, exact range and authenticated end-block hash. Every lookup still checks
the current authenticated ancestry and history bounds; failed, cancelled and
optimistic queries do not populate this cache. Prefetch uses a rolling window
of four candidates, retaining canonical output order when responses finish out
of order.

Finalized log headers persist under `<data-dir>/el/<network>/log-history` in
bounded RocksDB slots with synchronous WAL writes and a 1 GiB disk-write
admission limit; RocksDB compaction can temporarily exceed that disk footprint.
On restart, cached RLP headers are reattached to current
authenticated finality before use; saved metadata is not a trust anchor.
Missing or corrupt records are fetched and verified again. A single background
backfill combines outstanding requests and continues after an individual HTTP
wait times out, with a 30-minute worker deadline. A later request resumes
retained progress after failure. `/status.history` reports verified coverage,
the requested target, backfill activity and errors. A cold old-history request
can exceed the HTTP deadline while backfill continues; retry after coverage
reaches the requested range. Shutdown cancels and joins the worker before
closing its database.
`pending`, `safe` and `earliest` return an explicit unavailable error (`-32001`):
execution peers do not authenticate pending state, a distinct safe execution head
is not available, and genesis is outside the bounded history. The optimistic head can be reorganized,
whereas the finalized head is stable. Each state or block request pins its selected authenticated head,
including during a concurrent consensus update. Returned account and storage values are not
trusted until their ranges reconstruct the selected state/storage root. Authenticated account/storage absence
returns zero or empty code; an incomplete proof fails.

`eth_call` and `eth_estimateGas` accept a transaction object and an optional authenticated selector.
Omitting the selector uses the authenticated optimistic `"latest"` head, which must be available.
They fetch the selected execution header by its authenticated hash, then run
Nethermind's EVM against the corresponding state root. Accounts, storage slots
and bytecode are fetched from execution peers on demand and verified before a
clean execution attempt is resumed. A bounded 32 MiB cache retains demanded state.
Accounts are authenticated separately at each world-state root; verified code is
reused by code hash and storage values by authenticated storage root, account and
slot. Four calls may execute concurrently. Calls have a 10 million gas cap,
60-second deadline and at most 128 distinct state-fetch rounds. State changes
made by a call or estimate are discarded. `BLOCKHASH` uses the authenticated parent hash,
an anchored execution-header chain, or the verified EIP-2935 history-contract
storage as required by the active fork. Reverts return JSON-RPC error code `3`
with their return data. An estimate of a reverting transaction returns the same
error rather than a gas quantity. Blob and frame transactions use the selected
execution fork's existing conversion and EVM rules. Frame estimates fill omitted
execution/state limits through local replay; explicitly supplied limits determine
the fixed frame gas reservation. State overrides and pending state are unavailable.

`eth_getProof` accepts at most 64 storage keys. The returned account/storage
witnesses can be verified independently against the selected header's state root;
SNAP range witnesses are not returned as if they were path proofs.
`eth_feeHistory` accepts 1–16 blocks and at most 100 monotonically increasing
percentiles in `[0,100]`. Empty percentiles require only authenticated headers;
nonempty percentiles use verified receipt gas consumption, never a gas-limit
approximation. Fee suggestions use the gas-weighted 60th percentile of the
authenticated latest block's effective tips. They are local estimates, not
claims about a global pending transaction pool.

Transaction hash lookup scans the last 16 authenticated optimistic blocks and
16 finalized blocks, and consults a bounded observed index containing at most
32,768 transactions with a 20-minute idle expiry. Index entries are rechecked
against current authenticated ancestry before use, so an orphaned optimistic
transaction is not returned after a reorganization. A `null` response means
the transaction is not known in this scope; it does not prove global absence.
The ETH/SNAP protocols do not provide a complete mined hash-to-block index.
The index is rebuilt in memory after a restart.

Signed submission rejects malformed envelopes, wrong-chain signatures,
authenticated nonce regressions, insufficient balance, and underpriced fees.
Frame admission uses Nethermind's bounded validation-prefix execution over
verified payer and nonce-manager state. A bounded 32 MiB relay retains validated
submissions for 10 minutes so peers can fetch announced envelopes, blob sidecars
and cells through the existing ETH protocols. Submission returns an error when
there is no usable execution peer. The returned hash confirms local validation
and relay queueing; ETH has no admission acknowledgement, and inclusion must be
observed through an authenticated block or receipt. The client stores no signing
keys and does not sign transactions.

Example `eth_call` reading WETH `totalSupply()`:

```powershell
Invoke-RestMethod http://127.0.0.1:8545 -Method Post -ContentType application/json -Body '{"jsonrpc":"2.0","id":1,"method":"eth_call","params":[{"to":"0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2","data":"0x18160ddd"},"finalized"]}'
```

JSON-RPC 2.0 batches (up to 32 requests) and notifications are supported.
No request is forwarded without verification. Receipt/log queries fail when
any block body or receipt set is unavailable; log ranges are capped at 512
inclusive blocks and 10,000 matches.
Verified block, transaction, receipt, log, fee-history and proof results have an
8 MiB UTF-8 JSON limit. At most four execution-data requests may fetch and render
at once; excess work receives `-32005` immediately. HTTP batches have a 16 MiB
response budget, including a 512 KiB reserve for error envelopes. Earlier
responses are retained when the budget is reached; remaining requests receive
`-32005` without executing. Notifications still produce no response, and invalid
envelopes retain their standard errors. Oversized results fail explicitly rather
than returning incomplete data.
Normal HTTP requests, batches and WebSocket messages have a 64 KiB body limit.
A single HTTP `eth_sendRawTransaction` request may use up to 4 MiB plus 64 KiB
of JSON text for a raw envelope/sidecar of at most 2 MiB; at most four such large
requests run concurrently. Peer responses and proof nodes have separate size limits.
The listener admits at most 64 active requests and has no authentication; keep
it on loopback or place an authenticated gateway in front of it.
Each admitted request, including an entire batch, has a 90-second deadline;
the listener returns HTTP 504 if that deadline expires before a response starts.
The console shows each JSON-RPC method, its result, and its handling time. Batch
items are logged individually; malformed requests and oversized bodies are
logged without their contents. Routine ASP.NET Core request logs are suppressed.

## WebSocket subscriptions

Connect to `ws://127.0.0.1:8545/ws` and send standard JSON-RPC requests:

```json
{"jsonrpc":"2.0","id":1,"method":"eth_subscribe","params":["newHeads"]}
{"jsonrpc":"2.0","id":2,"method":"eth_subscribe","params":["logs",{"address":"0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2"}]}
```

`newHeads` publishes authenticated optimistic execution headers. `logs` accepts
the same address and positional topic filters as `eth_getLogs`, and publishes
only locally verified block receipts. On a reorganization, previously delivered
orphaned logs have `removed: true` before replacement logs are sent. Missing
blocks are verified by tracing parents from the next authenticated head; a
catch-up emits each intervening block's logs and only the newest head.
Subscriptions start after their acknowledgement and belong to their connection.
`eth_unsubscribe` takes the returned subscription ID. Pending transaction and
sync-status subscriptions are unavailable.

The host permits 16 connections and 16 subscriptions per connection. Each
connection has a 128-message, 1 MiB outbound queue, a 256 KiB message limit,
and a 10-second deadline to admit responses or a verified head's notifications.
Healthy bursts wait for the sender to drain the bounded queue; a stalled client
is disconnected independently of other clients. Verified reorganization history
is capped at 32 blocks and 16 MiB of encoded headers/logs. If that history cannot
repair a gap, execution data is unavailable, or a configured bound is exceeded,
the connection closes so it cannot silently continue an incomplete stream.
Reconnect, recover the affected range with `eth_getLogs`, then resubscribe.
WebSocket requests retain the 64 KiB input and 32-item batch limits; submit
large blob sidecars through HTTP.

## Direct P2P transport

The beacon side uses
[light-client bootstrap, updates-by-range, finality and optimistic req/resp](https://github.com/ethereum/consensus-specs/blob/master/specs/altair/light-client/p2p-interface.md)
with SSZ-snappy framing and fork-context checks. It also subscribes to
`light_client_finality_update` and `light_client_optimistic_update` gossip.
Fork topics are joined one epoch before activation and retired two epochs
afterwards, including blob-schedule digest changes. Gossip payloads are capped
at 16 KiB decoded and 32 pending validations; signatures and branches are
checked before accepted messages are forwarded. Updates share a serialized
consensus/journal owner with polling, and RPC publication reads only durably
recorded headers. Request/response repair runs every minute while healthy,
checking each beacon slot whether the retained optimistic head is more than
two slots old or in the future. A stale head triggers the next repair after at
most one slot of waiting. Committee catch-up runs first, followed by optimistic
and finality requests, while gossip advances the heads between repairs.
Each finality and optimistic repair poll waits for reconnecting peers within its
15-second request budget instead of failing on an empty peer snapshot.
Optimistic repair processes valid responses and tries the next peer when the
authenticated optimistic head does not advance, without penalizing that peer.
Finality repair accepts a valid response even when its finalized head is unchanged.
The execution side uses
[SNAP account/storage ranges and bytecode](https://github.com/ethereum/devp2p/blob/master/caps/snap.md).
It runs Nethermind's discv5 execution discovery, RLPx, `eth/68`–`eth/72` and
offers both `snap/1` and `snap/2`, with BAL requests gated by the block's fork. Execution proofs are
cached in memory; it does no full block synchronization. Peers can withhold
recent selected state; this affects availability, never the returned
value. The startup checkpoint remains a trusted input, whether supplied directly
or fetched from the selected HTTPS provider. Chain-spec bootnodes and default
ENRs seed discovery; direct public-network RLPx candidates come from discovered
peer records from discv5 or the signed DNS ENR tree.
The light client selects the signer-pinned `snap.<network>.ethdisco.net` tree
(`snap.mainnet.ethdisco.net`, `snap.hoodi.ethdisco.net` or
`snap.sepolia.ethdisco.net`) to discover SNAP-capable execution peers.
SNAP support is confirmed during the live P2P handshake.

Signed discovery ENRs are retained under `<data-dir>/el/<network>/discovery`
using the existing discovery storage and revalidated on restart. The cache keeps
at most 512 records; an oversized cache is preserved separately and discovery
continues with fresh seeds. A peer that fails to provide valid selected-state
data receives a bounded cooldown, while successful SNAP peers are tried first.
When connection capacity is full and fewer than four usable SNAP peers remain,
the client rotates one non-SNAP or failed SNAP connection to admit new candidates.
Incoming peer requests use two workers and a queue of at most 32 requests.

## Security and current scope

The consensus processor follows the [Altair light-client update lifecycle](https://ethereum.github.io/consensus-specs/specs/altair/light-client/sync-protocol/)
with [Electra](https://ethereum.github.io/consensus-specs/specs/electra/light-client/sync-protocol/)
and [Gloas](https://ethereum.github.io/consensus-specs/specs/gloas/light-client/sync-protocol/) proof indices.
It verifies bootstrap root and committee membership, execution inclusion,
finality and next-committee branches, participant public-key subgroups, aggregate
BLS signature and signing domain, slot order and committee-period continuity.
It refuses committee conflicts and skips. Signed optimistic updates can move
`latest`; timeout recovery can advance the sync committee, but never promotes
an unfinalized execution state to finalized RPC. Later finality is signed by
the committee that recovery selected, so it carries that recovery's weaker
trust; each forced advance is logged as a warning.

The client implements Electra, Fulu and Gloas wire formats with built-in
mainnet, Hoodi and Sepolia schedules. Gloas authenticates an execution block
hash through the payload bid; the client then retrieves the matching execution
header by hash before serving its state. Gloas has synthetic fork-boundary
tests and has authenticated native Sepolia bootstrap and subsequent optimistic
updates from connected peers.
Pre-Electra is refused. Future network schedules must be maintained locally
rather than accepted from peers.

This has the checkpoint and sync-committee security assumptions of an Ethereum
light client, rather than independent validation of every execution block.
Peers can withhold data, but forged proof results fail verification.
Synchronization retries with bounded per-request deadlines and retains the last verified head on
failure. Explicit `"finalized"` reads fail once verified finality is over one hour old;
optimistic `"latest"` reads require a signed head within ten current beacon slots. Local time must be accurate.
The verified committee chain and latest finalized head are persisted with
atomic writes and revalidated from the original checkpoint on restart.
The in-memory execution proof cache is not persisted. Availability depends on a
SNAP peer retaining the selected state root.

## Browser and mobile

Browser and mobile clients are future work. Direct browser P2P needs compatible
discovery and data transports, such as
[WebTransport](https://github.com/libp2p/specs/blob/master/webtransport/README.md);
the existing beacon QUIC and execution RLPx/SNAP listeners do not provide them.
Mobile packaging needs native crypto, AOT and ARM64 validation, suspend/resume,
background networking, storage recovery and battery-use checks.

## Validation

```powershell
dotnet build src/Nethermind/Nethermind.LightClient.Test/Nethermind.LightClient.Test.csproj -c release -p:SaveDiskSpace=true -warnaserror
dotnet src/Nethermind/artifacts/bin/Nethermind.LightClient.Test/release/Nethermind.LightClient.Test.dll --progress off
```

Tests use synthetic signed committees, independently constructed Merkle/RLP
fixtures, in-memory execution sources, and synthetic SNAP ranges. They exercise
tampering, key-bound absence, committee rollover, fork/signature boundaries,
range verification, block selectors, cancellation and JSON-RPC envelopes.

On 2026-10-08 the beacon P2P transport connected to live mainnet peers,
authenticated a checkpoint and advanced finality from execution block 26145883
to 26147224. Execution P2P discovered current peers through the mainnet ENR tree,
negotiated SNAP, and returned `0x301f02576d753a` wei for
`eth_getBalance(0xde198901C5ee4611142E0c61DfbbCEc7ab468F32, finalized)`
from a locally verified account-range proof. That result is
0.013544893799298362 ETH at the verified finalized block; balances change over time.

On 2026-10-10, with discv5-only execution discovery, a mainnet run reached
13 execution peers and 8 SNAP peers and returned verified `eth_getBalance`,
`eth_getCode` and WETH `eth_call` results. The client logged 183.0 ms,
253.6 ms and 382.5 ms for those requests, respectively.
