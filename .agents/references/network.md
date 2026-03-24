# Network Module Context

Knowledge specific to Nethermind.Network, P2P, synchronization, and discovery.

## Protocol handlers

- Use `HandleInBackground<T>` for message handlers that perform I/O or may throw.
  It catches exceptions and translates them to peer disconnects instead of crashing
  the handler pipeline. 9 protocol handler files use this pattern.

## Protocol handler registration

- Handlers expose static `Code` and `Version` via `IStaticProtocolInfo`
- Self-registration uses `RegisterWith(IProtocolRegistrar)` double-dispatch
- New handlers must implement both — no marker interface alternatives exist

## Message limits

- SnapSync RLP item-count limits (`SnapMessageLimits`) must accommodate `MaxResponseBytes`
  (3 MiB). Undersized limits disconnect and ban valid peers for 15 minutes, silently
  killing sync throughput. Calculate as MaxResponseBytes / minBytesPerItem with margin.

## CTS lifecycle

- Use `CancelDisposeAndClear(ref _cancellation)` for CancellationTokenSource fields.
  It atomically nulls the field via `Interlocked.CompareExchange`, then Cancel+Dispose
  on the captured value. Manual `Cancel(); Dispose()` patterns have race conditions.

## Peer management

- `ConcurrentDictionary.TryAdd` return value must gate subsequent side effects (metrics,
  event subscriptions). 38+ unchecked TryAdd calls exist vs 20 that check the return.
- `BlockAddedToMain` + `WasProcessed` guard is safer than `NewHeadBlock` for subscribers
  that persist derived state (snapshots, caches). `NewHeadBlock` fires only for the new
  tip — intermediate blocks are lost on reorgs.

## Sync feeds

- FastBlocks sync feeds (Bodies/Receipts/Headers) are structural clones. When copying
  one to create another, verify the `ISyncReport` counter matches the feed type —
  `ReceiptsSyncFeed` must use `FastBlocksReceipts`, not `FastBlocksBodies`.
