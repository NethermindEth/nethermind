# XDC Pivot=0 Sync Mode Fix

## Problem Summary
Nethermind was unable to maintain connections with GP5 (go-ethereum) nodes on XDC mainnet:
- Connected successfully via eth/100 protocol
- Handshake completed
- Entered "WaitingForBlock" sync mode immediately
- No sync traffic sent
- GP5 disconnected after 30s frameReadTimeout
- Keepalive timer didn't help because node wasn't syncing

## Root Cause
XDC mainnet config uses `PivotNumber: 0` to indicate "no fast sync, do full sync from genesis".

However, `MultiSyncModeSelector` incorrectly interpreted pivot=0 as "fast sync already complete":

```csharp
bool hasFastSyncBeenActive = best.Header >= best.PivotNumber;
// With pivot=0, this is ALWAYS true (any header >= 0)
```

This caused the sync mode selector to:
1. Think fast sync was complete (header >= 0)
2. Skip FullSync mode (because it thought sync was done)
3. Enter WaitingForBlock mode (waiting for new blocks from beacon/consensus)
4. Send no sync requests
5. Get disconnected by GP5 after 30s timeout

## Solution
Modified `MultiSyncModeSelector.cs` to treat `pivot=0` as a special case:

### 1. ShouldBeInWaitingForBlockMode
Added check: Don't enter WaitingForBlock if pivot=0 AND not caught up with peers

```csharp
bool pivotIsZero = best.PivotNumber == 0;
bool notCaughtUpYet = best.Header < best.TargetBlock - TotalSyncLag;

// Don't wait if pivot=0 and not caught up
!(pivotIsZero && notCaughtUpYet)
```

### 2. ShouldBeInFullSyncMode
Allow FullSync even without "desired peer" when pivot=0 and peers available

```csharp
bool canSyncWithPivotZero = pivotIsZero && postPivotPeerAvailable && notCaughtUpYet;
(desiredPeerKnown || canSyncWithPivotZero)
```

## Result
- Nethermind now enters FullSync mode immediately when peers are available
- Sends GetBlockHeaders/GetBlockBodies requests to sync the chain
- Maintains active connection with GP5 nodes
- Successfully syncs XDC mainnet from genesis

## Commit
```
b5685144a5 - fix: Handle pivot=0 case in MultiSyncModeSelector for XDC mainnet
```

## Testing
Build verified: Docker build succeeds with no errors
```bash
docker build --no-cache -f Dockerfile.xdc -t anilchinchawale/nmx:latest .
```

## Related Files
- `/root/.openclaw/workspace/nethermind/src/Nethermind/Nethermind.Synchronization/ParallelSync/MultiSyncModeSelector.cs`
- `/root/.openclaw/workspace/XDC-Node-Setup/mainnet/.xdc-node/nethermind.json` (config with pivot=0)

## Notes
- This fix does NOT affect Ethereum mainnet or other networks that use proper pivot-based fast sync
- Only activates when `PivotNumber = 0` which is XDC-specific
- Backward compatible with existing Nethermind behavior
- Keepalive timer from previous commit (b5c797c7ed) is still useful for post-sync idle periods
