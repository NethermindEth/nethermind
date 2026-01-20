# ProgressLogger for Trie Visitors

**Issue:** [#8504 - More use of ProgressLogger](https://github.com/NethermindEth/nethermind/issues/8504)
**Date:** 2026-01-20

## Problem

Long-running trie traversal operations (full pruning, trie verification, etc.) lack consistent progress reporting. The existing `ProgressLogger` utility is underutilized in these scenarios.

**Challenges:**
- Total node count is unknown upfront
- Traversal is parallelized and nodes are visited out of order
- Need a generic solution at the visitor level, not per-operation

## Solution: Path-Based Progress Estimation

Use the trie path to estimate progress. The state trie traverses a 256-bit keyspace in DFS order. The path prefix indicates position in the keyspace.

### Multi-Level Prefix Tracking

Track which path prefixes have been visited at multiple levels. Progress = visited prefixes / total possible prefixes at the most granular level with sufficient coverage.

| Level | Nibbles | Prefixes | Memory |
|-------|---------|----------|--------|
| 0 | 1 | 16 | 64 bytes |
| 1 | 2 | 256 | 1 KB |
| 2 | 3 | 4,096 | 16 KB |
| 3 | 4 | 65,536 | 256 KB |

**Total memory footprint:** ~273 KB

## Implementation

### New Class: VisitorProgressTracker

Location: `Nethermind.Trie/VisitorProgressTracker.cs`

```csharp
public class VisitorProgressTracker
{
    private const int MaxLevel = 3;

    private readonly int[][] _seen;
    private readonly int[] _seenCounts = new int[MaxLevel + 1];
    private readonly int[] _maxAtLevel = { 16, 256, 4096, 65536 };

    private long _nodeCount;
    private readonly ProgressLogger _logger;
    private readonly int _reportingInterval;

    public VisitorProgressTracker(
        string operationName,
        ILogManager logManager,
        int reportingInterval = 100_000)
    {
        _logger = new ProgressLogger(operationName, logManager);
        _logger.Reset(0, 100);
        _reportingInterval = reportingInterval;

        _seen = new int[MaxLevel + 1][];
        for (int level = 0; level <= MaxLevel; level++)
            _seen[level] = new int[_maxAtLevel[level]];
    }

    public void OnNodeVisited(in TreePath path)
    {
        int depth = Math.Min(path.Length, MaxLevel + 1);
        int prefix = 0;

        for (int level = 0; level < depth; level++)
        {
            prefix = (prefix << 4) | path[level];

            if (Interlocked.CompareExchange(ref _seen[level][prefix], 1, 0) == 0)
                Interlocked.Increment(ref _seenCounts[level]);
        }

        if (Interlocked.Increment(ref _nodeCount) % _reportingInterval == 0)
            LogProgress();
    }

    private void LogProgress()
    {
        // Use deepest level with >5% coverage for best granularity
        for (int level = MaxLevel; level >= 0; level--)
        {
            int seen = _seenCounts[level];
            if (seen > _maxAtLevel[level] / 20)
            {
                double progress = Math.Min((double)seen / _maxAtLevel[level], 1.0);
                _logger.Update((long)(progress * 100));
                _logger.LogProgress();
                return;
            }
        }

        _logger.Update((long)((double)_seenCounts[0] / 16 * 100));
        _logger.LogProgress();
    }

    public void Finish()
    {
        _logger.Update(100);
        _logger.MarkEnd();
        _logger.LogProgress();
    }
}
```

### Integration Points

#### 1. CopyTreeVisitor (Full Pruning)

File: `Nethermind.Blockchain/FullPruning/CopyTreeVisitor.cs`

```csharp
public class CopyTreeVisitor<TContext> : ITreeVisitor<TContext>
    where TContext : struct, ITreePathContextWithStorage, INodeContext<TContext>
{
    private readonly VisitorProgressTracker _progressTracker;

    public CopyTreeVisitor(..., ILogManager logManager)
    {
        _progressTracker = new VisitorProgressTracker("Full Pruning", logManager);
    }

    private void PersistNode(Hash256 storage, in TreePath path, TrieNode node)
    {
        _concurrentWriteBatcher.Set(storage, path, node.Keccak, node.FullRlp.Span, _writeFlags);
        _progressTracker.OnNodeVisited(path);
    }

    public void Finish()
    {
        _progressTracker.Finish();
    }
}
```

#### 2. TrieStatsCollector (Trie Verification)

File: `Nethermind.Trie/TrieStatsCollector.cs`

```csharp
public class TrieStatsCollector : ITreeVisitor<TrieStatsCollector.Context>
{
    private readonly VisitorProgressTracker? _progressTracker;

    public TrieStatsCollector(..., ILogManager? logManager = null)
    {
        if (logManager is not null)
            _progressTracker = new VisitorProgressTracker("Trie Verification", logManager);
    }

    public void VisitLeaf(in Context nodeContext, TrieNode node)
    {
        // ... existing logic ...
        _progressTracker?.OnNodeVisited(nodeContext.Path);
    }
}
```

## How Progress Estimation Works

1. **Concurrent-safe**: Uses `Interlocked.CompareExchange` to mark prefixes as seen
2. **Adaptive granularity**: Uses the deepest level with >5% coverage
3. **Monotonically increasing**: Progress only goes up as more prefixes are seen
4. **Path-based**: Derives progress from WHERE we are in the keyspace, not node count

### Example Progression

```
Early:    Level 0 - seen 4/16 (25%)     → Reports 25%
Mid:      Level 2 - seen 1800/4096 (44%) → Reports 44%
Late:     Level 3 - seen 58000/65536 (88%) → Reports 88%
Complete: Finish() called              → Reports 100%
```

## Files to Modify

| File | Change |
|------|--------|
| `Nethermind.Trie/VisitorProgressTracker.cs` | **NEW** - Progress tracking utility |
| `Nethermind.Blockchain/FullPruning/CopyTreeVisitor.cs` | Add progress tracking |
| `Nethermind.Trie/TrieStatsCollector.cs` | Add progress tracking |

## Testing

1. Unit tests for `VisitorProgressTracker`:
   - Verify prefix tracking at each level
   - Verify thread-safety with concurrent calls
   - Verify progress estimation accuracy

2. Integration tests:
   - Verify progress output during full pruning
   - Verify progress output during trie verification

## Future Extensions

- Add to `HistoryPruner` for historical block pruning
- Add to `EraImporter` for era file imports
- Consider adding `maxHints` parameter for known trie sizes
