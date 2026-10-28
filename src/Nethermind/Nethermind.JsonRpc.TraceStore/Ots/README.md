# OTS account history

TraceStore provides a partial Otterscan API:

- `ots_getApiLevel` returns **0**, not a claim of full Otterscan compatibility.
- `ots_searchTransactionsBefore(address, blockNumber, pageSize)`
- `ots_searchTransactionsAfter(address, blockNumber, pageSize)`
- `ots_traceTransaction(transactionHash)`

Enable `TraceStore.Enabled`, include `Trace` in `TraceStore.TraceTypes`, and explicitly add `Ots` to `JsonRpc.EnabledModules`. Retain receipts (`Receipt.StoreReceipts`) and transaction lookup history (`Receipt.TxLookupLimit = 0` for unrestricted lookup). Set `TraceStore.BlocksToKeep = 0` to retain traces indefinitely; this does not backfill traces for blocks processed before TraceStore was enabled. Historical queries require the corresponding canonical block bodies, stored traces and receipts. Historical state is not required.

## Search semantics

Block cursors are exclusive. A before cursor of zero starts at the head; an after cursor of zero starts after genesis. Page sizes range from 1 to 25 transactions, but all matches in the final matching block are included, so a page can exceed the requested size. Results are ordered by descending block number and transaction index, with corresponding receipts in the same order. Receipt timestamps are decimal numbers.

Search matches senders, destinations and created addresses anywhere in the stored transaction call tree, including zero-value and reverted calls. Each matching transaction appears once. Logs alone and block rewards do not count as call involvement. These results describe calls, not committed ETH transfers.

A further matching block is read to determine whether another page exists. `firstPage` identifies the newest end and `lastPage` the oldest end of the result history. When paging backwards, use the oldest returned block as the next cursor; when paging forwards, use the newest returned block.

## Traces and limitations

Transaction traces are depth-first call-tree entries, including creations and selfdestructs but excluding precompile entries. Reverted calls are included. `STATICCALL` and `DELEGATECALL` have a null value; creation output contains deployed code.

Searches scan blocks and deserialize stored traces without an address index. Sparse or long-range searches can exceed `JsonRpc.Timeout`; there is no guarantee that an arbitrary historical search completes within that timeout.

Missing or inconsistent traces, blocks or required receipts produce errors rather than incomplete pages or a false end-of-history indication. Empty blocks do not require stored transaction traces. Canonical-chain changes detected during a request also produce an error; retry the request. An unknown transaction produces a not-found error.
