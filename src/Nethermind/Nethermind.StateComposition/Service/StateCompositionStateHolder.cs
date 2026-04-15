// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;
using Nethermind.StateComposition.Visitors;

namespace Nethermind.StateComposition.Service;

/// <summary>
/// Thread-safe store of baseline scan results and scan lifecycle state.
/// </summary>
internal sealed class StateCompositionStateHolder
{
    private readonly Lock _lock = new();

    private StateCompositionStats _currentStats;
    private TrieDepthDistribution _currentDistribution;
    private ScanMetadata? _lastScanMetadata;
    private bool _isInitialized;

    private CumulativeSizeStats? _incrementalStats;
    private readonly CumulativeDepthStats _currentDepthStats = new();
    private long _incrementalBlock;
    private int _diffsSinceBaseline;
    private Hash256? _lastProcessedStateRoot;

    // Incremental trackers — keep the state needed to update CodeBytesTotal and
    // SlotCountHistogram when a TrieDiff lands. Seeded from each full scan, carried
    // forward across diffs, and persisted in the snapshot so a restart doesn't lose
    // them. All mutation happens under _lock together with _incrementalStats, so the
    // histogram and the tracker maps never drift relative to each other.
    private Dictionary<ValueHash256, long> _slotCountByAddress = new();
    private Dictionary<ValueHash256, int> _codeHashRefcounts = new();
    private Dictionary<ValueHash256, int> _codeHashSizes = new();

    public StateCompositionStats CurrentStats
    {
        get { lock (_lock) return _currentStats; }
    }

    public TrieDepthDistribution CurrentDistribution
    {
        get { lock (_lock) return _currentDistribution; }
    }

    public ScanMetadata? LastScanMetadata
    {
        get { lock (_lock) return _lastScanMetadata; }
    }

    public bool IsInitialized { get { lock (_lock) return _isInitialized; } }

    public CumulativeSizeStats? IncrementalStats
    {
        get { lock (_lock) return _incrementalStats; }
    }

    /// <summary>
    /// Returns a cloned snapshot of the current depth stats under the holder's lock.
    /// Callers (RPC + Metrics.UpdateDepthDistribution) iterate all 9 long[16] fields;
    /// returning a clone eliminates torn reads from concurrent AddInPlace calls.
    /// </summary>
    public CumulativeDepthStats CurrentDepthStats
    {
        get { lock (_lock) return _currentDepthStats.Clone(); }
    }

    public long IncrementalBlock
    {
        get { lock (_lock) return _incrementalBlock; }
    }

    public int DiffsSinceBaseline
    {
        get { lock (_lock) return _diffsSinceBaseline; }
    }

    public Hash256? LastProcessedStateRoot
    {
        get { lock (_lock) return _lastProcessedStateRoot; }
    }

    /// <summary>
    /// Build the RPC <see cref="CachedStatsResponse"/> under a single <see cref="_lock"/> entry
    /// so the four fields (stats, block, diff count, scan metadata) cannot tear against a
    /// concurrent diff application.
    /// </summary>
    public CachedStatsResponse BuildCachedStatsResponse()
    {
        lock (_lock)
        {
            return new CachedStatsResponse
            {
                CurrentStats = _incrementalStats,
                BlockNumber = _incrementalStats is not null ? _incrementalBlock : null,
                DiffsSinceLastScan = _diffsSinceBaseline,
                LastScanMetadata = _lastScanMetadata,
            };
        }
    }

    /// <summary>
    /// Build a fully-populated <see cref="StateCompositionSnapshot"/> atomically. Stats,
    /// baseline metadata, depth stats, and the three tracker dictionaries are all captured
    /// under a single <see cref="_lock"/> entry so the persisted snapshot cannot tear
    /// against a concurrent <see cref="InitializeIncremental"/> or diff application.
    /// </summary>
    public StateCompositionSnapshot BuildSnapshot(
        CumulativeSizeStats stats,
        long blockNumber,
        Hash256 stateRoot)
    {
        lock (_lock)
        {
            return new StateCompositionSnapshot(
                stats,
                blockNumber,
                stateRoot,
                _diffsSinceBaseline,
                _lastScanMetadata?.BlockNumber ?? 0,
                _currentDepthStats.Clone(),
                new Dictionary<ValueHash256, long>(_slotCountByAddress),
                new Dictionary<ValueHash256, int>(_codeHashRefcounts),
                new Dictionary<ValueHash256, int>(_codeHashSizes));
        }
    }

    public void SetBaseline(StateCompositionStats stats, TrieDepthDistribution dist)
    {
        lock (_lock)
        {
            _currentStats = stats;
            _currentDistribution = dist;
            _isInitialized = true;
        }
    }

    public void MarkScanCompleted(long blockNumber, Hash256 stateRoot, TimeSpan duration)
    {
        lock (_lock)
        {
            _lastScanMetadata = new ScanMetadata
            {
                BlockNumber = blockNumber,
                StateRoot = stateRoot,
                CompletedAt = DateTimeOffset.UtcNow,
                Duration = duration,
                IsComplete = true,
            };
        }
    }

    public void InitializeIncremental(CumulativeSizeStats baseline, long blockNumber, Hash256 stateRoot,
        TrieDepthDistribution? depthDistribution = null,
        IReadOnlyDictionary<ValueHash256, long>? slotCountByAddress = null,
        IReadOnlyDictionary<ValueHash256, int>? codeHashRefcounts = null,
        IReadOnlyDictionary<ValueHash256, int>? codeHashSizes = null)
    {
        lock (_lock)
        {
            _incrementalStats = baseline;
            _incrementalBlock = blockNumber;
            _diffsSinceBaseline = 0;
            _lastProcessedStateRoot = stateRoot;
            _currentDepthStats.Reset();
            if (depthDistribution.HasValue)
                _currentDepthStats.SeedFromScan(depthDistribution.Value);

            _slotCountByAddress = slotCountByAddress is null
                ? new Dictionary<ValueHash256, long>()
                : new Dictionary<ValueHash256, long>(slotCountByAddress);
            _codeHashRefcounts = codeHashRefcounts is null
                ? new Dictionary<ValueHash256, int>()
                : new Dictionary<ValueHash256, int>(codeHashRefcounts);
            _codeHashSizes = codeHashSizes is null
                ? new Dictionary<ValueHash256, int>()
                : new Dictionary<ValueHash256, int>(codeHashSizes);
        }
    }

    /// <summary>
    /// Drop the incremental baseline root so OnNewHeadBlock stops spawning diff
    /// tasks until a fresh scan reseeds via <see cref="InitializeIncremental"/>.
    /// Deliberately narrow: cached stats, depth distribution, and incremental
    /// trackers stay visible to RPC until <c>PublishScanResults</c> swaps them.
    /// </summary>
    public void InvalidateBaseline()
    {
        lock (_lock) _lastProcessedStateRoot = null;
    }

    /// <summary>
    /// Apply a <see cref="TrieDiff"/> atomically: updates the cumulative stats,
    /// the per-address slot tracker (and the slot-count histogram), and the
    /// per-code-hash refcount/size trackers (and <see cref="CumulativeSizeStats.CodeBytesTotal"/>).
    /// All mutation happens under <see cref="_lock"/> so callers see a consistent
    /// view across every field.
    /// <para>
    /// <paramref name="codeSizeLookup"/> is invoked once per previously-unseen code
    /// hash referenced on the "new" side of a <see cref="CodeHashChange"/>. The
    /// returned value is cached in the tracker for later refcount-0 decrements.
    /// </para>
    /// </summary>
    public CumulativeSizeStats ApplyIncrementalDiffAndUpdate(
        TrieDiff diff, long blockNumber, Hash256 stateRoot,
        Func<ValueHash256, int> codeSizeLookup)
    {
        lock (_lock)
        {
            CumulativeSizeStats current = _incrementalStats!.Value;
            CumulativeSizeStats updated = current.ApplyDiff(diff);

            long codeBytes = updated.CodeBytesTotal;

            // Histogram copy-on-write: keep the baseline ImmutableArray untouched so
            // callers that captured it before this diff still see the pre-diff values.
            long[] histogram = new long[CumulativeSizeStats.SlotHistogramLength];
            if (!updated.SlotCountHistogram.IsDefault)
                updated.SlotCountHistogram.CopyTo(histogram);

            if (diff.CodeHashChanges is not null)
            {
                foreach (CodeHashChange change in diff.CodeHashChanges)
                {
                    ApplyCodeHashChange(change, codeSizeLookup, ref codeBytes);
                }
            }

            if (diff.SlotCountChanges is not null)
            {
                foreach (SlotCountChange change in diff.SlotCountChanges)
                {
                    ApplySlotCountChange(change, histogram);
                }
            }

            updated = updated with
            {
                CodeBytesTotal = codeBytes,
                SlotCountHistogram = System.Collections.Immutable.ImmutableArray.Create(histogram),
            };

            _incrementalStats = updated;
            _incrementalBlock = blockNumber;
            _diffsSinceBaseline++;
            _lastProcessedStateRoot = stateRoot;
            if (diff.DepthDelta is not null)
                _currentDepthStats.AddInPlace(diff.DepthDelta);

            return updated;
        }
    }

    /// <summary>
    /// Apply one <see cref="CodeHashChange"/>: decrement the old code hash's refcount
    /// (freeing its contribution to <paramref name="codeBytes"/> when it hits zero) and
    /// increment the new code hash's refcount (resolving its size on first reference).
    /// </summary>
    private void ApplyCodeHashChange(
        in CodeHashChange change,
        Func<ValueHash256, int> codeSizeLookup,
        ref long codeBytes)
    {
        if (change.HadCode)
        {
            _codeHashRefcounts.TryGetValue(change.OldCodeHash, out int oldRefcount);
            if (oldRefcount <= 1)
            {
                _codeHashRefcounts.Remove(change.OldCodeHash);
                if (_codeHashSizes.Remove(change.OldCodeHash, out int oldSize))
                    codeBytes -= oldSize;
            }
            else
            {
                _codeHashRefcounts[change.OldCodeHash] = oldRefcount - 1;
            }
        }

        if (change.HasCode)
        {
            _codeHashRefcounts.TryGetValue(change.NewCodeHash, out int newRefcount);
            if (newRefcount == 0)
            {
                int size = codeSizeLookup(change.NewCodeHash);
                _codeHashSizes[change.NewCodeHash] = size;
                codeBytes += size;
            }
            _codeHashRefcounts[change.NewCodeHash] = newRefcount + 1;
        }
    }

    /// <summary>
    /// Apply one <see cref="SlotCountChange"/>: move the contract between histogram
    /// buckets. Contracts with no prior entry enter the histogram; contracts whose
    /// slot count drops to zero leave it. The histogram therefore always counts
    /// exactly the contracts in <see cref="_slotCountByAddress"/>.
    /// </summary>
    private void ApplySlotCountChange(in SlotCountChange change, long[] histogram)
    {
        bool hadEntry = _slotCountByAddress.TryGetValue(change.AddressHash, out long oldCount);
        long newCount = oldCount + change.SlotDelta;

        if (hadEntry)
        {
            int oldBucket = VisitorCounters.ComputeSlotBucket(oldCount);
            histogram[oldBucket]--;
        }

        if (newCount > 0)
        {
            int newBucket = VisitorCounters.ComputeSlotBucket(newCount);
            histogram[newBucket]++;
            _slotCountByAddress[change.AddressHash] = newCount;
        }
        else
        {
            _slotCountByAddress.Remove(change.AddressHash);
        }
    }

    public void RestoreFromSnapshot(StateCompositionSnapshot snapshot)
    {
        lock (_lock)
        {
            _incrementalStats = snapshot.Stats;
            _incrementalBlock = snapshot.BlockNumber;
            _diffsSinceBaseline = snapshot.DiffsSinceBaseline;
            _lastProcessedStateRoot = snapshot.StateRoot;
            // _isInitialized stays false — baseline scan data (TopN, distribution)
            // is not persisted. getCachedStats() returns incremental stats;
            // getTrieDistribution() requires a fresh scan.

            _currentDepthStats.Reset();
            if (snapshot.DepthStats is { IsSeeded: true } persisted)
                _currentDepthStats.SeedFromSnapshot(persisted);

            _slotCountByAddress = snapshot.SlotCountByAddress is null
                ? new Dictionary<ValueHash256, long>()
                : new Dictionary<ValueHash256, long>(snapshot.SlotCountByAddress);
            _codeHashRefcounts = snapshot.CodeHashRefcounts is null
                ? new Dictionary<ValueHash256, int>()
                : new Dictionary<ValueHash256, int>(snapshot.CodeHashRefcounts);
            _codeHashSizes = snapshot.CodeHashSizes is null
                ? new Dictionary<ValueHash256, int>()
                : new Dictionary<ValueHash256, int>(snapshot.CodeHashSizes);
        }
    }
}
