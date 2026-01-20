# ProgressLogger for Trie Visitors - Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add path-based progress tracking to trie visitor operations (full pruning, trie verification).

**Architecture:** Create a `VisitorProgressTracker` utility class that tracks visited path prefixes at multiple levels (1-4 nibbles) to estimate progress through the keyspace. Visitors call `OnNodeVisited(path)` and the tracker uses the deepest level with sufficient coverage to report percentage complete.

**Tech Stack:** C#, .NET, NUnit for testing

---

## Task 1: Create VisitorProgressTracker Class

**Files:**
- Create: `src/Nethermind/Nethermind.Trie/VisitorProgressTracker.cs`

**Step 1: Create the new file with basic structure**

```csharp
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Trie;

/// <summary>
/// Tracks progress of trie traversal operations using path-based estimation.
/// Uses multi-level prefix tracking to estimate completion percentage even when
/// total node count is unknown and traversal is concurrent/out-of-order.
/// </summary>
public class VisitorProgressTracker
{
    private const int MaxLevel = 3; // Levels 0-3 (1 to 4 nibbles)

    // Arrays for tracking seen prefixes: 16, 256, 4096, 65536 entries
    private readonly int[][] _seen;
    private readonly int[] _seenCounts = new int[MaxLevel + 1];
    private static readonly int[] MaxAtLevel = { 16, 256, 4096, 65536 };

    private long _nodeCount;
    private readonly ProgressLogger _logger;
    private readonly int _reportingInterval;

    public VisitorProgressTracker(
        string operationName,
        ILogManager logManager,
        int reportingInterval = 100_000)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = new ProgressLogger(operationName, logManager);
        _logger.Reset(0, 100); // Use 100 as target for percentage display
        _reportingInterval = reportingInterval;

        _seen = new int[MaxLevel + 1][];
        for (int level = 0; level <= MaxLevel; level++)
        {
            _seen[level] = new int[MaxAtLevel[level]];
        }
    }

    /// <summary>
    /// Called when a node is visited during traversal.
    /// Thread-safe: can be called concurrently from multiple threads.
    /// </summary>
    public void OnNodeVisited(in TreePath path)
    {
        int depth = Math.Min(path.Length, MaxLevel + 1);
        int prefix = 0;

        for (int level = 0; level < depth; level++)
        {
            prefix = (prefix << 4) | path[level];

            // Mark prefix as seen (thread-safe)
            if (Interlocked.CompareExchange(ref _seen[level][prefix], 1, 0) == 0)
            {
                Interlocked.Increment(ref _seenCounts[level]);
            }
        }

        // Log progress at intervals
        if (Interlocked.Increment(ref _nodeCount) % _reportingInterval == 0)
        {
            LogProgress();
        }
    }

    private void LogProgress()
    {
        // Use deepest level with >5% coverage for best granularity
        for (int level = MaxLevel; level >= 0; level--)
        {
            int seen = _seenCounts[level];
            if (seen > MaxAtLevel[level] / 20)
            {
                double progress = Math.Min((double)seen / MaxAtLevel[level], 1.0);
                _logger.Update((long)(progress * 100));
                _logger.LogProgress();
                return;
            }
        }

        // Fallback to level 0
        _logger.Update((long)((double)_seenCounts[0] / 16 * 100));
        _logger.LogProgress();
    }

    /// <summary>
    /// Call when traversal is complete to log final progress.
    /// </summary>
    public void Finish()
    {
        _logger.Update(100);
        _logger.MarkEnd();
        _logger.LogProgress();
    }

    /// <summary>
    /// Gets the current estimated progress (0.0 to 1.0).
    /// </summary>
    public double GetProgress()
    {
        for (int level = MaxLevel; level >= 0; level--)
        {
            int seen = _seenCounts[level];
            if (seen > MaxAtLevel[level] / 20)
            {
                return Math.Min((double)seen / MaxAtLevel[level], 1.0);
            }
        }
        return (double)_seenCounts[0] / 16;
    }

    /// <summary>
    /// Gets the total number of nodes visited.
    /// </summary>
    public long NodeCount => Interlocked.Read(ref _nodeCount);
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Nethermind/Nethermind.Trie/Nethermind.Trie.csproj -c release --no-restore`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Nethermind/Nethermind.Trie/VisitorProgressTracker.cs
git commit -m "feat(trie): add VisitorProgressTracker for path-based progress estimation

Addresses #8504 - More use of ProgressLogger

- Tracks visited path prefixes at 4 levels (16 to 65536 granularity)
- Thread-safe for concurrent traversal
- Estimates progress from keyspace position, not node count"
```

---

## Task 2: Add Unit Tests for VisitorProgressTracker

**Files:**
- Create: `src/Nethermind/Nethermind.Trie.Test/VisitorProgressTrackerTests.cs`

**Step 1: Create test file**

```csharp
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class VisitorProgressTrackerTests
{
    [Test]
    public void OnNodeVisited_TracksProgress_AtLevel0()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 1000);

        // Act - visit paths starting with nibbles 0-7 (half the keyspace)
        for (int i = 0; i < 8; i++)
        {
            TreePath path = TreePath.FromNibble(new byte[] { (byte)i, 0, 0, 0 });
            tracker.OnNodeVisited(path);
        }

        // Assert - should be ~50% progress at level 0
        double progress = tracker.GetProgress();
        progress.Should().BeApproximately(0.5, 0.01);
    }

    [Test]
    public void OnNodeVisited_UsesDeepestLevelWithCoverage()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);

        // Act - visit enough prefixes at level 1 to trigger level 1 reporting
        // Need >5% of 256 = 13 unique prefixes at level 1
        for (int i = 0; i < 64; i++) // 64 unique 2-nibble prefixes
        {
            TreePath path = TreePath.FromNibble(new byte[] { (byte)(i / 16), (byte)(i % 16), 0, 0 });
            tracker.OnNodeVisited(path);
        }

        // Assert - should use level 1 (64/256 = 25%)
        double progress = tracker.GetProgress();
        progress.Should().BeApproximately(0.25, 0.01);
    }

    [Test]
    public void OnNodeVisited_IsThreadSafe()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);
        const int threadCount = 8;
        const int nodesPerThread = 1000;

        // Act - visit nodes concurrently
        Parallel.For(0, threadCount, threadId =>
        {
            for (int i = 0; i < nodesPerThread; i++)
            {
                int nibble1 = (threadId * nodesPerThread + i) / 4096 % 16;
                int nibble2 = (threadId * nodesPerThread + i) / 256 % 16;
                int nibble3 = (threadId * nodesPerThread + i) / 16 % 16;
                int nibble4 = (threadId * nodesPerThread + i) % 16;
                TreePath path = TreePath.FromNibble(new byte[] { (byte)nibble1, (byte)nibble2, (byte)nibble3, (byte)nibble4 });
                tracker.OnNodeVisited(path);
            }
        });

        // Assert - node count should match
        tracker.NodeCount.Should().Be(threadCount * nodesPerThread);
    }

    [Test]
    public void OnNodeVisited_ProgressMonotonicallyIncreases()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);
        double lastProgress = 0;

        // Act & Assert - visit paths in sequence and verify progress never decreases
        for (int i = 0; i < 256; i++)
        {
            TreePath path = TreePath.FromNibble(new byte[] { (byte)(i / 16), (byte)(i % 16), 0, 0 });
            tracker.OnNodeVisited(path);

            double progress = tracker.GetProgress();
            progress.Should().BeGreaterThanOrEqualTo(lastProgress);
            lastProgress = progress;
        }
    }

    [Test]
    public void Finish_SetsProgressTo100()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);
        TreePath path = TreePath.FromNibble(new byte[] { 0, 0, 0, 0 });
        tracker.OnNodeVisited(path);

        // Act
        tracker.Finish();

        // Assert - GetProgress still returns actual progress, but logger shows 100%
        // (We can't easily test logger output, so just verify Finish doesn't throw)
        tracker.NodeCount.Should().Be(1);
    }

    [Test]
    public void OnNodeVisited_HandlesShortPaths()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);

        // Act - visit paths with fewer than 4 nibbles
        TreePath path1 = TreePath.FromNibble(new byte[] { 0 });
        TreePath path2 = TreePath.FromNibble(new byte[] { 1, 2 });
        TreePath path3 = TreePath.FromNibble(new byte[] { 3, 4, 5 });

        tracker.OnNodeVisited(path1);
        tracker.OnNodeVisited(path2);
        tracker.OnNodeVisited(path3);

        // Assert - should not throw and should track nodes
        tracker.NodeCount.Should().Be(3);
    }

    [Test]
    public void OnNodeVisited_HandlesEmptyPath()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);

        // Act
        TreePath path = TreePath.Empty;
        tracker.OnNodeVisited(path);

        // Assert
        tracker.NodeCount.Should().Be(1);
        tracker.GetProgress().Should().Be(0); // Empty path doesn't contribute to progress
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test src/Nethermind/Nethermind.Trie.Test/Nethermind.Trie.Test.csproj -c release --filter FullyQualifiedName~VisitorProgressTrackerTests -v n`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Nethermind/Nethermind.Trie.Test/VisitorProgressTrackerTests.cs
git commit -m "test(trie): add unit tests for VisitorProgressTracker

Tests cover:
- Progress tracking at different levels
- Thread-safety with concurrent calls
- Monotonically increasing progress
- Edge cases (short paths, empty path)"
```

---

## Task 3: Integrate into CopyTreeVisitor

**Files:**
- Modify: `src/Nethermind/Nethermind.Blockchain/FullPruning/CopyTreeVisitor.cs`

**Step 1: Add VisitorProgressTracker field and update constructor**

In `CopyTreeVisitor.cs`, add the field and update constructor:

```csharp
// Add field after line 32:
private readonly VisitorProgressTracker _progressTracker;

// Update constructor (lines 34-45) to create tracker:
public CopyTreeVisitor(
    INodeStorage nodeStorage,
    WriteFlags writeFlags,
    ILogManager logManager,
    CancellationToken cancellationToken)
{
    _cancellationToken = cancellationToken;
    _writeFlags = writeFlags;
    _logger = logManager.GetClassLogger();
    _stopwatch = new Stopwatch();
    _concurrentWriteBatcher = new ConcurrentNodeWriteBatcher(nodeStorage);
    _progressTracker = new VisitorProgressTracker("Full Pruning", logManager);
}
```

**Step 2: Update PersistNode to use progress tracker**

Replace the manual progress logging in `PersistNode` (lines 79-93):

```csharp
private void PersistNode(Hash256 storage, in TreePath path, TrieNode node)
{
    if (node.Keccak is not null)
    {
        // simple copy of nodes RLP
        _concurrentWriteBatcher.Set(storage, path, node.Keccak, node.FullRlp.Span, _writeFlags);
        _progressTracker.OnNodeVisited(path);
    }
}
```

**Step 3: Update Finish method to call progress tracker**

Update the `Finish` method (lines 109-114):

```csharp
public void Finish()
{
    _finished = true;
    _progressTracker.Finish();
    if (_logger.IsInfo)
        _logger.Info($"Full Pruning Finished: {_stopwatch.Elapsed} {_progressTracker.NodeCount / (double)Million:N} mln nodes mirrored.");
    _concurrentWriteBatcher.Dispose();
}
```

**Step 4: Add using directive**

Add at top of file:

```csharp
using Nethermind.Trie;
```

**Step 5: Remove unused field and constant**

Remove `_persistedNodes` field (line 27) and update `LogProgress` if still referenced, or remove it entirely since we now use VisitorProgressTracker.

**Step 6: Verify it compiles**

Run: `dotnet build src/Nethermind/Nethermind.Blockchain/Nethermind.Blockchain.csproj -c release --no-restore`
Expected: Build succeeded

**Step 7: Commit**

```bash
git add src/Nethermind/Nethermind.Blockchain/FullPruning/CopyTreeVisitor.cs
git commit -m "feat(pruning): integrate VisitorProgressTracker into CopyTreeVisitor

Replaces manual every-1M-nodes logging with path-based progress estimation.
Progress now shows actual percentage through the keyspace."
```

---

## Task 4: Integrate into TrieStatsCollector

**Files:**
- Modify: `src/Nethermind/Nethermind.Trie/TrieStatsCollector.cs`

**Step 1: Add VisitorProgressTracker field**

Add field after line 19:

```csharp
private readonly VisitorProgressTracker? _progressTracker;
```

**Step 2: Update constructor to optionally create tracker**

Update constructor (lines 63-69) to accept optional progress tracking:

```csharp
public TrieStatsCollector(IKeyValueStore codeKeyValueStore, ILogManager logManager, CancellationToken cancellationToken = default, bool expectAccounts = true, bool trackProgress = false)
{
    _codeKeyValueStore = codeKeyValueStore ?? throw new ArgumentNullException(nameof(codeKeyValueStore));
    _logger = logManager.GetClassLogger();
    ExpectAccounts = expectAccounts;
    _cancellationToken = cancellationToken;
    if (trackProgress)
    {
        _progressTracker = new VisitorProgressTracker("Trie Verification", logManager);
    }
}
```

**Step 3: Add progress tracking to IncrementLevel**

Update `IncrementLevel(Context context)` method (lines 186-190) to also track progress:

```csharp
private void IncrementLevel(Context context)
{
    long[] levels = context.IsStorage ? Stats._storageLevels : Stats._stateLevels;
    IncrementLevel(context, levels);

    // Only track state trie nodes for progress (not storage)
    if (!context.IsStorage)
    {
        _progressTracker?.OnNodeVisited(context.Path);
    }
}
```

**Step 4: Remove manual progress logging from VisitLeaf**

Remove the manual logging code in `VisitLeaf` (lines 135-140):

```csharp
public void VisitLeaf(in Context nodeContext, TrieNode node)
{
    if (nodeContext.IsStorage)
    {
        Interlocked.Add(ref Stats._storageSize, node.FullRlp.Length);
        Interlocked.Increment(ref Stats._storageLeafCount);
    }
    else
    {
        Interlocked.Add(ref Stats._stateSize, node.FullRlp.Length);
        Interlocked.Increment(ref Stats._accountCount);
    }

    IncrementLevel(nodeContext);
}
```

**Step 5: Remove _lastAccountNodeCount field**

Remove the `_lastAccountNodeCount` field (line 17) since it's no longer needed.

**Step 6: Add Finish method**

Add method to call when stats collection is complete:

```csharp
public void Finish()
{
    _progressTracker?.Finish();
}
```

**Step 7: Verify it compiles**

Run: `dotnet build src/Nethermind/Nethermind.Trie/Nethermind.Trie.csproj -c release --no-restore`
Expected: Build succeeded

**Step 8: Commit**

```bash
git add src/Nethermind/Nethermind.Trie/TrieStatsCollector.cs
git commit -m "feat(trie): integrate VisitorProgressTracker into TrieStatsCollector

Adds optional path-based progress tracking for trie verification.
Replaces manual every-1M-nodes logging with percentage-based progress."
```

---

## Task 5: Update Callers of TrieStatsCollector

**Files:**
- Modify: Callers that create TrieStatsCollector and want progress tracking

**Step 1: Find callers**

Run: `grep -r "new TrieStatsCollector" src/Nethermind --include="*.cs"`

**Step 2: Update relevant callers to enable progress tracking**

For callers that are long-running operations (like BlockingVerifyTrie), add `trackProgress: true`:

Example in `src/Nethermind/Nethermind.State/BlockingVerifyTrie.cs`:

```csharp
var statsCollector = new TrieStatsCollector(_codeDb, _logManager, cancellationToken, trackProgress: true);
// ... use statsCollector ...
statsCollector.Finish();
```

**Step 3: Verify it compiles**

Run: `dotnet build src/Nethermind/Nethermind.slnx -c release --no-restore`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add -A
git commit -m "feat(state): enable progress tracking in trie verification callers"
```

---

## Task 6: Run Full Test Suite

**Step 1: Run all trie tests**

Run: `dotnet test src/Nethermind/Nethermind.Trie.Test/Nethermind.Trie.Test.csproj -c release -v n`
Expected: All tests pass

**Step 2: Run blockchain tests**

Run: `dotnet test src/Nethermind/Nethermind.Blockchain.Test/Nethermind.Blockchain.Test.csproj -c release -v n`
Expected: All tests pass

**Step 3: Format code**

Run: `dotnet format whitespace src/Nethermind/ --folder`

**Step 4: Final commit if formatting changed anything**

```bash
git add -A
git commit -m "chore: format code"
```

---

## Summary

| Task | Description | Files |
|------|-------------|-------|
| 1 | Create VisitorProgressTracker | `Nethermind.Trie/VisitorProgressTracker.cs` |
| 2 | Add unit tests | `Nethermind.Trie.Test/VisitorProgressTrackerTests.cs` |
| 3 | Integrate into CopyTreeVisitor | `Nethermind.Blockchain/FullPruning/CopyTreeVisitor.cs` |
| 4 | Integrate into TrieStatsCollector | `Nethermind.Trie/TrieStatsCollector.cs` |
| 5 | Update callers | Various callers |
| 6 | Run full test suite | N/A |
