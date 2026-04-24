// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;
using Nethermind.StateComposition.Visitors;

namespace Nethermind.StateComposition.Service;

internal sealed class StateCompositionStateHolder
{
    private readonly Lock _lock = new();

    private StateCompositionStats _currentStats;
    private TrieDepthDistribution _currentDistribution;
    private ScanMetadata _lastScanMetadata;
    private bool _hasScanBaseline;

    private CumulativeTrieStats _incrementalStats;
    private bool _hasIncrementalBaseline;
    private readonly CumulativeDepthStats _currentDepthStats = new();
    private long _incrementalBlock;
    private int _diffsSinceBaseline;
    // Sentinel: Hash256.Zero means "no baseline" (cold start or post-invalidation).
    // OnNewHeadBlock and RunIncrementalDiff gate on this directly instead of nullables.
    private Hash256 _lastProcessedStateRoot = Hash256.Zero;

    // Incremental trackers — keep the state needed to update CodeBytesTotal and
    // SlotCountHistogram when a TrieDiff lands. Seeded from each full scan, carried
    // forward across diffs, and persisted in the snapshot so a restart doesn't lose
    // them. All mutation happens under _lock together with _incrementalStats, so the
    // histogram and the tracker maps never drift relative to each other.
    private Dictionary<ValueHash256, long> _slotCountByAddress = [];
    private Dictionary<ValueHash256, int> _codeHashRefcounts = [];
    private Dictionary<ValueHash256, int> _codeHashSizes = [];

    public StateCompositionStats CurrentStats { get { lock (_lock) return _currentStats; } }

    public TrieDepthDistribution CurrentDistribution { get { lock (_lock) return _currentDistribution; } }

    public ScanMetadata LastScanMetadata { get { lock (_lock) return _lastScanMetadata; } }

    public bool HasScanBaseline { get { lock (_lock) return _hasScanBaseline; } }

    public bool HasIncrementalBaseline { get { lock (_lock) return _hasIncrementalBaseline; } }

    public CumulativeTrieStats IncrementalStats { get { lock (_lock) return _incrementalStats; } }

    /// <summary>
    /// Returns the live cumulative depth stats reference. Callers MUST hold
    /// <c>StateCompositionService._diffLock</c> for the whole duration they
    /// read the returned instance; that is the only thing excluding the two
    /// writers, <see cref="ApplyIncrementalDiffAndUpdate"/> and
    /// <see cref="PublishScanBaseline"/>, which themselves run inside <c>_diffLock</c>
    /// critical sections in <c>RunIncrementalDiff</c> and <c>PublishScanResults</c>.
    /// Returning the live ref (instead of copying) avoids an O(9×16) copy on every
    /// diff; readers iterate immediately and do not cache the reference past publish.
    /// </summary>
    internal CumulativeDepthStats CurrentDepthStats => _currentDepthStats;

    public long IncrementalBlock { get { lock (_lock) return _incrementalBlock; } }

    public int DiffsSinceBaseline { get { lock (_lock) return _diffsSinceBaseline; } }

    /// <summary>
    /// Returns <see cref="Hash256.Zero"/> when no baseline is available
    /// (cold start or post-<see cref="InvalidateBaseline"/>).
    /// </summary>
    public Hash256 LastProcessedStateRoot { get { lock (_lock) return _lastProcessedStateRoot; } }

    public StateCompositionReport BuildReport()
    {
        lock (_lock)
        {
            return new StateCompositionReport
            {
                TrieStats = _incrementalStats,
                TrieDistribution = _currentDistribution,
                BlockNumber = _incrementalBlock,
                DiffsSinceBaseline = _diffsSinceBaseline,
                LastScanMetadata = _lastScanMetadata,
            };
        }
    }

    /// <summary>
    /// Build a snapshot that points at the live tracker state. The caller serializes
    /// the returned record synchronously (see <see cref="Snapshots.StateCompositionSnapshotStore.WriteSnapshot"/>)
    /// under the single-writer invariant, so handing out the underlying references
    /// is safe and avoids copying three dictionaries and a <c>long[9][16]</c> grid.
    /// </summary>
    public StateCompositionSnapshot BuildSnapshot(
        CumulativeTrieStats stats,
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
                _lastScanMetadata.BlockNumber,
                _currentDepthStats,
                _slotCountByAddress,
                _codeHashRefcounts,
                _codeHashSizes);
        }
    }

    public void SetBaseline(StateCompositionStats stats, TrieDepthDistribution dist)
    {
        lock (_lock)
        {
            _currentStats = stats;
            _currentDistribution = dist;
            _hasScanBaseline = true;
        }
    }

    public void MarkScanCompleted(long blockNumber, Hash256 stateRoot, TimeSpan duration, bool isComplete)
    {
        lock (_lock)
        {
            _lastScanMetadata = new ScanMetadata
            {
                BlockNumber = blockNumber,
                StateRoot = stateRoot,
                CompletedAt = DateTimeOffset.UtcNow,
                Duration = duration,
                IsComplete = isComplete,
            };
        }
    }

    /// <summary>
    /// Atomically publish a fresh scan's baseline. Coalesces the three mutations
    /// (<see cref="SetBaseline"/>, <see cref="MarkScanCompleted"/>, <see cref="InitializeIncremental"/>)
    /// under a single <c>_lock</c> acquisition so <see cref="BuildReport"/> cannot
    /// observe a torn state in which the new distribution and scan metadata are
    /// visible but <c>_incrementalStats</c> still reflects the pre-scan cumulative
    /// state.
    /// </summary>
    public void PublishScanBaseline(
        StateCompositionStats stats,
        TrieDepthDistribution dist,
        long blockNumber,
        Hash256 stateRoot,
        TimeSpan duration,
        bool isComplete,
        CumulativeTrieStats cumulativeBaseline,
        Dictionary<ValueHash256, long>? slotCountByAddress = null,
        Dictionary<ValueHash256, int>? codeHashRefcounts = null,
        Dictionary<ValueHash256, int>? codeHashSizes = null)
    {
        lock (_lock)
        {
            _currentStats = stats;
            _currentDistribution = dist;
            _hasScanBaseline = true;

            _lastScanMetadata = new ScanMetadata
            {
                BlockNumber = blockNumber,
                StateRoot = stateRoot,
                CompletedAt = DateTimeOffset.UtcNow,
                Duration = duration,
                IsComplete = isComplete,
            };

            _incrementalStats = cumulativeBaseline;
            _hasIncrementalBaseline = true;
            _incrementalBlock = blockNumber;
            _diffsSinceBaseline = 0;
            _lastProcessedStateRoot = stateRoot;
            _currentDepthStats.Reset();
            _currentDepthStats.SeedFromScan(dist);

            _slotCountByAddress = slotCountByAddress ?? [];
            _codeHashRefcounts = codeHashRefcounts ?? [];
            _codeHashSizes = codeHashSizes ?? [];
        }
    }

    /// <summary>
    /// Atomic read of the state queried during shutdown flush. Returns <c>false</c>
    /// when no incremental baseline is present or the state root has been
    /// invalidated (<see cref="Hash256.Zero"/> sentinel).
    /// </summary>
    public bool TryGetShutdownSnapshot(out Hash256 stateRoot, out long blockNumber, out CumulativeTrieStats stats)
    {
        lock (_lock)
        {
            if (!_hasIncrementalBaseline || _lastProcessedStateRoot == Hash256.Zero)
            {
                stateRoot = Hash256.Zero;
                blockNumber = 0;
                stats = default;
                return false;
            }

            stateRoot = _lastProcessedStateRoot;
            blockNumber = _incrementalBlock;
            stats = _incrementalStats;
            return true;
        }
    }

    public void InitializeIncremental(CumulativeTrieStats baseline, long blockNumber, Hash256 stateRoot,
        TrieDepthDistribution? depthDistribution = null,
        Dictionary<ValueHash256, long>? slotCountByAddress = null,
        Dictionary<ValueHash256, int>? codeHashRefcounts = null,
        Dictionary<ValueHash256, int>? codeHashSizes = null)
    {
        lock (_lock)
        {
            _incrementalStats = baseline;
            _hasIncrementalBaseline = true;
            _incrementalBlock = blockNumber;
            _diffsSinceBaseline = 0;
            _lastProcessedStateRoot = stateRoot;
            _currentDepthStats.Reset();
            if (depthDistribution.HasValue)
                _currentDepthStats.SeedFromScan(depthDistribution.Value);

            // Caller hands ownership of the tracker maps to the holder. The visitor
            // produces them once at scan completion and no longer references them;
            // decoder-loaded maps are freshly materialized per decode call.
            _slotCountByAddress = slotCountByAddress ?? [];
            _codeHashRefcounts = codeHashRefcounts ?? [];
            _codeHashSizes = codeHashSizes ?? [];
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
        lock (_lock) _lastProcessedStateRoot = Hash256.Zero;
    }

    /// <summary>
    /// Apply a <see cref="TrieDiff"/> atomically: updates the cumulative stats,
    /// the per-address slot tracker (and the slot-count histogram), and the
    /// per-code-hash refcount/size trackers (and <see cref="CumulativeTrieStats.CodeBytesTotal"/>).
    /// All mutation happens under <see cref="_lock"/> so callers see a consistent
    /// view across every field.
    /// <para>
    /// <paramref name="codeSizeLookup"/> is invoked once per previously-unseen code
    /// hash referenced on the "new" side of a <see cref="CodeHashChange"/>. The
    /// returned value is cached in the tracker for later refcount-0 decrements.
    /// </para>
    /// </summary>
    public CumulativeTrieStats ApplyIncrementalDiffAndUpdate(
        TrieDiff diff, long blockNumber, Hash256 stateRoot,
        Func<ValueHash256, int> codeSizeLookup)
    {
        lock (_lock)
        {
            CumulativeTrieStats updated = _incrementalStats.ApplyDiff(diff);

            long codeBytes = updated.CodeBytesTotal;

            // Histogram copy-on-write: build a fresh ImmutableArray so callers that
            // captured the baseline before this diff still see the pre-diff values.
            // CreateBuilder(16) + MoveToImmutable() hands the backing array to the
            // ImmutableArray without a second allocation/copy pass.
            ImmutableArray<long>.Builder histogram =
                ImmutableArray.CreateBuilder<long>(CumulativeTrieStats.SlotHistogramLength);
            histogram.Count = CumulativeTrieStats.SlotHistogramLength;
            if (!updated.SlotCountHistogram.IsDefault)
            {
                for (int i = 0; i < CumulativeTrieStats.SlotHistogramLength; i++)
                    histogram[i] = updated.SlotCountHistogram[i];
            }

            foreach (CodeHashChange change in diff.CodeHashChanges)
            {
                ApplyCodeHashChange(change, codeSizeLookup, ref codeBytes);
            }

            foreach (SlotCountChange change in diff.SlotCountChanges)
            {
                ApplySlotCountChange(change, histogram);
            }

            updated = updated with
            {
                CodeBytesTotal = codeBytes,
                SlotCountHistogram = histogram.MoveToImmutable(),
            };

            _incrementalStats = updated;
            _incrementalBlock = blockNumber;
            _diffsSinceBaseline++;
            _lastProcessedStateRoot = stateRoot;
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
            ref int newRefcount = ref CollectionsMarshal.GetValueRefOrAddDefault(_codeHashRefcounts, change.NewCodeHash, out bool existed);
            if (!existed)
            {
                int size = codeSizeLookup(change.NewCodeHash);
                _codeHashSizes[change.NewCodeHash] = size;
                codeBytes += size;
            }
            newRefcount++;
        }
    }

    /// <summary>
    /// Apply one <see cref="SlotCountChange"/>: move the contract between histogram
    /// buckets. Contracts with no prior entry enter the histogram; contracts whose
    /// slot count drops to zero leave it. The histogram therefore always counts
    /// exactly the contracts in <see cref="_slotCountByAddress"/>.
    /// </summary>
    private void ApplySlotCountChange(in SlotCountChange change, ImmutableArray<long>.Builder histogram)
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
            _hasIncrementalBaseline = true;
            _incrementalBlock = snapshot.BlockNumber;
            _diffsSinceBaseline = snapshot.DiffsSinceBaseline;
            _lastProcessedStateRoot = snapshot.StateRoot;
            // _hasScanBaseline stays false — baseline scan data (TopN, distribution)
            // is not persisted. statecomp_get() returns incremental stats;
            // depth distribution requires a fresh scan.

            _currentDepthStats.Reset();
            if (snapshot.DepthStats.IsSeeded)
                _currentDepthStats.SeedFromSnapshot(snapshot.DepthStats);

            // Take ownership of the decoder-allocated dictionaries directly. The
            // decoder materializes fresh maps per call and does not retain them,
            // so no other writer can observe or mutate these instances.
            _slotCountByAddress = snapshot.SlotCountByAddress;
            _codeHashRefcounts = snapshot.CodeHashRefcounts;
            _codeHashSizes = snapshot.CodeHashSizes;
        }
    }
}
