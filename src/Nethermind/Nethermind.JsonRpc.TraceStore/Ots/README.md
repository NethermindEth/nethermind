# OTS account-history subset

This implements the four methods used by micro-eth-signer's explorer/history library:

- `ots_getApiLevel`
- `ots_searchTransactionsBefore(address, blockNumber, pageSize)`
- `ots_searchTransactionsAfter(address, blockNumber, pageSize)`
- `ots_traceTransaction(transactionHash)`

It is an opt-in subset, not full Otterscan support. Searches scan by default;
a separately opt-in trace-address index accelerates certified historical ranges.
`ots_getApiLevel` returns the JSON number **0** to avoid advertising Erigon's full API level 8.
Zero is a partial-support marker, not a claim of a released Otterscan compatibility level.
Clients must only use the methods listed above. The pinned micro-eth-signer client probes
whether `ots_getApiLevel` succeeds, so it recognizes this subset without pretending that
the other OTS endpoints exist.

## Configuration and historical coverage

Enable `TraceStore.Enabled` and **add** `Ots` to the existing `JsonRpc.EnabledModules` list.
The namespace is not enabled by default. `TraceStore.TraceTypes` must include `Trace`.
To retain all subsequently executed traces, set `TraceStore.BlocksToKeep=0`.

The requested range also needs retained block bodies and receipts. Transaction trace lookup
needs the receipt transaction-hash index (`Receipt.TxLookupLimit=0` keeps that index).
Receipt/history pruning can still make a query unavailable even if its trace exists.
Historical state versions are not needed: this module never falls back to EVM replay.

Enabling TraceStore does **not** backfill old traces. In particular, snap-syncing bodies and
receipts does not produce traces for those blocks. A missing non-empty block's trace causes
an RPC error, not an empty result or a terminal page. This also applies when looking past a
full page to determine whether another matching block exists.

## Search semantics

- The cursor block is excluded. For `Before`, zero starts at the captured chain head;
  for `After`, zero starts after genesis. OTS cursors accept JSON numbers even with
  `JsonRpc.StrictHexFormat=true`; they are not Ethereum QUANTITY parameters.
- `Before(...,1,...)` returns no transactions: genesis has no user transactions.
  This follows the published exclusive-cursor contract rather than Erigon 3.7.1's
  legacy behavior of restarting at the chain tip for this cursor.
- Both methods return transactions in descending block/transaction-index order.
  `After` chooses the nearest subsequent page before reversing it.
- Page sizes are 1–25. All matches in the last selected block are included, so a page
  can contain more transactions than its requested size. This avoids losing transactions
  when the client advances its block cursor.
- Transactions are deduplicated by their position in the block, regardless of how many
  matching calls they contain. A self-call is returned once.
- Matching means **call involvement**, including zero-value calls, creations,
  selfdestructs and reverted calls. It does not mean a successful ETH transfer.
- Rewards and other non-transaction trace entries are excluded. Receiving a token event
  without being a call participant is not an OTS match; clients must also search logs.
- Receipts retain the normal Ethereum receipt fields/logs and add a decimal `timestamp`.
- `Before(...,0,...)` marks `firstPage`; reaching the end of the backward search marks
  `lastPage`. `After(...,0,...)` marks `lastPage`; reaching the end of the forward search
  marks `firstPage`. A further matching block is sought before declaring the searched
  side complete.
- A detected canonical-chain change returns an error asking the client to retry.

`ots_traceTransaction` emits a depth-first array with uppercase call types and depth zero
for the root. Precompiles are omitted. `STATICCALL` and `DELEGATECALL` have null `value`;
creations return their deployed code as output, and recorded revert output is preserved.
The OTS trace format has no per-frame success/error flag. Use the Parity trace API when
accounting for reverted descendants of otherwise successful transactions. Older stored
traces cannot recover information that their original tracer did not record.

## How this uses Nethermind storage

The lookup path is:

1. `IBlockFinder` resolves a canonical block number/hash and its transaction body.
2. The existing TraceStore database maps **block hash → compressed call trees**.
   `ParityLikeTraceSerializer` decompresses that block's `ParityLikeTxTrace` records.
3. The module checks the call trees for address involvement and retains only matching
   transactions. It does not call the range-eager `trace_filter` implementation.
4. `IReceiptFinder` reads/reconstructs stored receipt metadata; the existing transaction
   and receipt RPC DTOs supply normal wire fields, gas pricing and log indexes.
5. A known transaction hash is located through `IReceiptFinder.FindBlockHash`, then its
   trace is selected from that block's stored call trees.

Search holds one block's decoded traces at a time, plus the result page. Requests check
`JsonRpc.Timeout` at block/action boundaries, and the RPC pool allows two concurrent
exclusive OTS searches/traces. A single database read/decompression is not interruptible.
Sparse or absent-address searches outside index coverage remain linear in the number of
scanned blocks and can time out. Enabling an index does not make incomplete history complete.

## Optional trace-address index

Set `TraceStore.AddressIndexEnabled=true` in addition to the configuration above.
The index requires `BlocksToKeep=0` and `TraceTypes` including `Trace`; incompatible
settings fail activation rather than silently indexing pruned history. It does not accelerate
the Parity `trace_filter` method or committed-value accounting.

- `AddressIndexFromBlock` defaults to **1**. Set a later start for known recent trace coverage;
  earlier requests still scan and can fail on unavailable traces. A later start is not a claim
  that the address had no earlier history.
- `AddressIndexLag` defaults to **64 blocks** behind the head. The uncovered tail is scanned.
  This reduces ordinary reorg invalidations but is **not finality**. Zero is useful for fixtures.
- `AddressIndexReset` defaults to **false**. After checkpoint invalidation, restart once with
  it enabled, then disable it. Only the owned trace-address database is reset.

The plugin owns a separate RocksDB database named `TraceAddressIndex`, under
`traceAddressIndex`. It reuses `LogIndexStorage` compression/merge/compaction machinery
and directly populates `LogIndexAggregate.Address` from stored call trees. **No event-index
records or fabricated logs are used.** Addresses are the union of sender, destination and
creation-result addresses, including reverted, zero-value and precompile calls. Rewards are
excluded. There is no migration of existing TraceStore or LogIndex databases.

A single background writer starts after block processing initialization and certifies consecutive
canonical blocks. Transaction hash/position/block identities and trace completeness are
validated before indexing. Empty blocks can advance coverage without a trace record. Missing
or inconsistent data pauses at the gap; it never jumps over it. Malformed deserialization or
storage exceptions disable acceleration. Coverage/progress and pauses are logged.

Each indexed block's hash and parent hash are written before its postings and min/max
coverage batch in the same WAL. Postings/min/max are atomic across columns. These writes
are WAL-synced before a v2 checkpoint containing range, genesis hash and canonical end hash
is written and WAL-synced in the **same database**. Coverage becomes visible afterward.
Uncheckpointed identities are ignored; older checkpoint schemas require an index reset.
A crash between these operations leaves a postings/checkpoint mismatch: restart disables
acceleration until reset. Repaired-database markers, altered start settings and noncanonical
checkpoints also disable it. This is fail-closed acceleration with a scan fallback, not automatic
reorg repair or protection against arbitrary manual database edits.

Queries use postings only inside certified coverage and still validate candidate blocks'
traces and receipts. Missing candidate data is an error. Each positive is bound to its persisted
indexed block hash. Both scan/index boundaries are linked through persisted parent/end hashes,
including empty candidate sets. This matters because canonical chain-level caches can publish
a reorg progressively while the old head and coverage endpoint still appear canonical; individual
`IsMainChain` checks alone do not protect jumps. Checkpoint identity is also checked before
selection and again before returning, along with returned transaction blocks. A reorg below the checkpoint disables the index until reset;
a reorg in the unindexed tail is handled by the scan path. Disposal stops/awaits the writer
and readers before releasing the index database; the borrowed TraceStore is not disposed.

### Initial implementation limits

The builder processes one decoded block at a time, up to 64 blocks before a one-second
pause. It uses two WAL syncs per block; backfill throughput is deliberately conservative and
has not been validated over a full mainnet history. Per-height identities add a 5-byte key and
64-byte value per indexed block, before storage overhead/compression. Memory is one block's traces/address set,
not a queued full-range aggregation. Stored decompression and a native DB operation are not
interruptible; cancellation is checked between blocks, transactions, actions and postings.

The existing posting iterator is ascending. Forward selection stops after at most 26 candidate
blocks. Backward selection walks the requested address's postings and keeps only a **26-block
tail**, then reverses it. This bounds the candidate buffer, but costs **O(address candidate
blocks in the covered range)** per backward page, not O(page size). It avoids unrelated trace
deserialization; it is not yet an efficient native reverse posting iterator. Existing storage
iterator/compression buffers have their own bounds. Block numbers beyond `int.MaxValue`
remain outside index coverage and are scanned.

These limits and incomplete trace coverage remain important for sparse/full-history latency.
A tiny controlled chain does not establish Paul's mainnet account-history latency claim.

## Tests

```sh
dotnet test --project src/Nethermind/Nethermind.JsonRpc.TraceStore.Test/Nethermind.JsonRpc.TraceStore.Test.csproj -c release
```

`OtsRpcModuleTests` exercises production DI/storage, request binding and serialization,
pagination, missing data, timeouts, reorgs, call conversion, and actual EVM execution with
successful and reverted internal calls, failed children inside successful transactions,
CREATE/CREATE2, SELFDESTRUCT before/after EIP-6780, and multi-page traversal without
omissions or duplicates. Request binding is tested with the production strict-quantity
setting. No mock OTS service is used.

`OtsRpcModuleTests.Index` uses the actual RocksDB-backed index and production plugin DI.
It compares indexed and scanned pagination in both directions, verifies skipped unrelated
trace reads, candidate/coverage errors, gap recovery, restart/checkpoint/repair rejection,
reorgs, event-index isolation, unsupported configurations, cancellation and active-worker
disposal. The unchanged Paul-library asset workload also passes with coverage certified
through the fixture head; all five library phase outputs match the scan baseline exactly.

A bounded differential check against Erigon 3.7.1 independently executed the same
20-block fixtures under Shanghai and Osaka. Per fork, 217 of 229 OTS result payloads
matched exactly, including all 19 transaction traces. The remaining 12 results were
solely the `Before(...,1,...)` legacy divergence described above. This is controlled-chain
coverage, not a mainnet-wide or full-OTS compatibility claim.
