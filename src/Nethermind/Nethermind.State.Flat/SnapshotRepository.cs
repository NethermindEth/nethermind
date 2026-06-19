// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Collections.Pooled;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat;

/// <summary>
/// Owns both tiers: the in-memory snapshots (base + compacted dictionaries) and the persisted tier
/// (four <see cref="PersistedSnapshotBucket"/>s over the arena/blob/catalog stores). Two-tier graph
/// walks, persistence, and compaction-assembly live here so they operate on the buckets directly.
/// </summary>
public class SnapshotRepository : ISnapshotRepository, IDisposable
{
    private readonly ILogger _logger;

    // ---- Persisted tier: four buckets keyed by StateId.To. Each bucket is self-contained and
    // individually-locked. A `To` can live in more than one bucket (a base and a compacted snapshot
    // can share it).
    private readonly ISnapshotCatalog _catalog;
    private readonly int _compactSize;
    private readonly PersistedSnapshotBucket _base;
    private readonly PersistedSnapshotBucket _smallCompacted;
    private readonly PersistedSnapshotBucket _largeCompacted;
    private readonly PersistedSnapshotBucket _compactSized;
    private int _disposed;

    // ---- In-memory tier: only the recent unpersisted snapshots (bounded by
    // MaxInMemoryBaseSnapshotCount). Aggregates are kept as running totals at the TryAdd* /
    // RemoveAndRelease* sites rather than via ConcurrentDictionary.Count.
    private readonly ConcurrentDictionary<StateId, Snapshot> _compactedSnapshots = new();
    private readonly ConcurrentDictionary<StateId, Snapshot> _snapshots = new();
    private long _snapshotCount;
    private long _compactedSnapshotCount;
    private readonly ReadWriteLockBox<SortedSet<StateId>> _sortedSnapshotStateIds = new([]);

    // StateId is larger than a machine word, so its read/write across threads must be synchronized.
    private readonly Lock _lastCommittedLock = new();
    private StateId _lastCommittedStateId;
    private bool _hasLastCommitted;

    public SnapshotRepository(
        IArenaManager arenaManager,
        BlobArenaManager blobArenaManager,
        ISnapshotCatalog catalog,
        IFlatDbConfig config,
        ILogManager logManager)
    {
        _catalog = catalog;
        _logger = logManager.GetClassLogger<SnapshotRepository>();
        _base = new PersistedSnapshotBucket(_catalog, SnapshotTier.PersistedBase, _logger);
        _smallCompacted = new PersistedSnapshotBucket(_catalog, SnapshotTier.PersistedSmallCompacted, _logger);
        _largeCompacted = new PersistedSnapshotBucket(_catalog, SnapshotTier.PersistedLargeCompacted, _logger);
        _compactSized = new PersistedSnapshotBucket(_catalog, SnapshotTier.PersistedCompactSized, _logger);
        _compactSize = config.CompactSize;
    }

    public int SnapshotCount => (int)Interlocked.Read(ref _snapshotCount);
    // Test-only; not part of ISnapshotRepository.
    internal int CompactedSnapshotCount => (int)Interlocked.Read(ref _compactedSnapshotCount);

    public int PersistedSnapshotCount => (int)(_base.Count + _smallCompacted.Count + _largeCompacted.Count + _compactSized.Count);

    public void AddStateId(in StateId stateId)
    {
        using (_sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
            sortedSnapshots.Add(stateId);
    }

    public AssembledSnapshotResult AssembleSnapshots(in StateId baseBlock, in StateId targetState, int estimatedSize)
    {
        if (baseBlock == targetState) return new AssembledSnapshotResult(SnapshotPooledList.Empty(), PersistedSnapshotList.Empty());

        AssemblePolicy policy = new(targetState);
        return WalkAndAssemble(baseBlock, estimatedSize, ref policy);
    }

    /// <summary>
    /// BFS over the snapshot graph from <paramref name="baseBlock"/> back toward
    /// <paramref name="minBlockNumber"/>, returning the in-memory snapshots along the winning path in
    /// ascending order (<c>result[0].From</c> is the terminus, <c>result[^1].To == baseBlock</c>).
    /// Empty when no path reaches the terminus.
    /// </summary>
    /// <remarks>
    /// Each node has up to 2 edges, explored widest-jump first (compacted, then base). Edges dropping
    /// below <paramref name="minBlockNumber"/> are pruned, so an overshooting compacted jump yields to
    /// the narrower base edge. Wins at the first node reaching <paramref name="minBlockNumber"/>.
    /// </remarks>
    public SnapshotPooledList AssembleInMemorySnapshotsForCompaction(in StateId baseBlock, long minBlockNumber, int estimatedSize)
    {
        InMemoryCompactionPolicy policy = new(minBlockNumber);
        AssembledSnapshotResult result = WalkAndAssemble(baseBlock, estimatedSize, ref policy);
        result.Persisted.Dispose(); // in-memory-only policy yields no persisted entries
        return result.InMemory;
    }

    /// <summary>
    /// Find the next snapshot to flush — the valid persist candidate directly extending
    /// <paramref name="currentPersistedState"/> (its <c>From</c> equals it). Returns the leased persisted
    /// or in-memory snapshot (caller disposes), or <c>(null, null)</c> when none is reachable. Used by
    /// both persistence phases in <see cref="PersistenceManager"/>.
    /// </summary>
    /// <remarks>
    /// Runs <see cref="WalkAndAssemble{TPolicy}"/> with <see cref="FindPersistPolicy"/>, navigating
    /// <c>From</c>-edges from <paramref name="seed"/> down toward <paramref name="currentPersistedState"/>
    /// and winning at the first candidate edge reaching it. Non-candidate tiers are traversed as
    /// skip-pointers. The winning candidate is the chain's terminus; this re-leases just that snapshot
    /// and drops the rest.
    /// </remarks>
    public (PersistedSnapshot? Persisted, Snapshot? InMemory) FindSnapshotToPersist(
        in StateId seed, in StateId currentPersistedState, int compactSize)
    {
        if (seed.BlockNumber <= currentPersistedState.BlockNumber) return (null, null);

        int estimatedSize = (int)Math.Clamp(seed.BlockNumber - currentPersistedState.BlockNumber, 4, 4096);
        FindPersistPolicy policy = new(currentPersistedState, compactSize);
        using AssembledSnapshotResult result = WalkAndAssemble(seed, estimatedSize, ref policy);

        // Candidate is the chain terminus (oldest); re-lease it and let the `using` drop the rest. The
        // in-mem-before-persisted invariant puts a persisted candidate at Persisted[0], in-memory at InMemory[0].
        if (result.Persisted.Count > 0)
        {
            PersistedSnapshot persisted = result.Persisted[0];
            persisted.TryAcquire();
            return (persisted, null);
        }
        if (result.InMemory.Count > 0)
        {
            Snapshot inMemory = result.InMemory[0];
            inMemory.TryAcquire();
            return (null, inMemory);
        }
        return (null, null);
    }

    /// <summary>
    /// Best-effort backward BFS over the persisted tier from <paramref name="toStateId"/>, returning the
    /// contiguous chain reaching the deepest block <c>&gt;= </c><paramref name="minBlockNumber"/>
    /// (oldest-first). Need not be fully populated; empty when fewer than two snapshots are found.
    /// </summary>
    public PersistedSnapshotList AssemblePersistedSnapshotsForCompaction(in StateId toStateId, long minBlockNumber)
    {
        int estimatedSize = (int)Math.Clamp(toStateId.BlockNumber - minBlockNumber, 4, 4096);
        PersistedCompactionPolicy policy = new(minBlockNumber);
        AssembledSnapshotResult result = WalkAndAssemble(toStateId, estimatedSize, ref policy);
        result.InMemory.Dispose(); // persisted-only policy yields no in-memory entries

        PersistedSnapshotList persisted = result.Persisted;
        if (persisted.Count < 2)
        {
            persisted.Dispose();
            return PersistedSnapshotList.Empty();
        }
        return persisted;
    }

    public bool TryLeaseInMemoryState(in StateId stateId, SnapshotTier tier, [NotNullWhen(true)] out Snapshot? entry)
    {
        tier.EnsureInMemory();
        ConcurrentDictionary<StateId, Snapshot> snapshots = tier == SnapshotTier.InMemoryBase ? _snapshots : _compactedSnapshots;
        SpinWait sw = new();
        while (snapshots.TryGetValue(stateId, out entry))
        {
            if (entry.TryAcquire()) return true;

            sw.SpinOnce();
        }
        return false;
    }

    public bool TryAdd(Snapshot snapshot, SnapshotTier tier)
    {
        tier.EnsureInMemory();
        if (tier == SnapshotTier.InMemoryBase)
        {
            if (_snapshots.TryAdd(snapshot.To, snapshot))
            {
                Interlocked.Increment(ref _snapshotCount);
                Metrics.SnapshotCount++;

                long totalBytes = snapshot.EstimateMemory();
                Metrics.SnapshotMemory += totalBytes;
                Metrics.TotalSnapshotMemory += totalBytes;

                return true;
            }

            return false;
        }

        if (_compactedSnapshots.TryAdd(snapshot.To, snapshot))
        {
            Interlocked.Increment(ref _compactedSnapshotCount);
            Metrics.CompactedSnapshotCount++;

            long compactedBytes = snapshot.Content.EstimateCompactedMemory();
            Metrics.CompactedSnapshotMemory += compactedBytes;
            Metrics.TotalSnapshotMemory += compactedBytes;

            return true;
        }

        return false;
    }

    public ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        StateId min = new(blockNumber, ValueKeccak.Zero);
        StateId max = new(blockNumber, ValueKeccak.MaxValue);

        return sortedSnapshots.GetViewBetween(min, max).ToPooledList(0);
    }

    private bool HasForkAt(long blockNumber)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        StateId min = new(blockNumber, ValueKeccak.Zero);
        StateId max = new(blockNumber, ValueKeccak.MaxValue);

        return sortedSnapshots.GetViewBetween(min, max).Count > 1;
    }

    public StateId? GetLastSnapshotId()
    {
        StateId? max;
        using (_sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots))
            max = sortedSnapshots.Count == 0 ? null : sortedSnapshots.Max;

        // Persisted tips aren't in `_sortedSnapshotStateIds`, and after a reorg the persisted tier can hold
        // an orphan above the in-memory tip — so fold the persisted maxima in for the true cross-tier max
        // that callers (flush bound, orphan-walk bound) need.
        // Regression: RemoveSiblingAndDescendents_PersistedOrphanAboveInMemoryTip_IsPruned.
        max = MaxState(max, _base.Max);
        max = MaxState(max, _smallCompacted.Max);
        max = MaxState(max, _largeCompacted.Max);
        max = MaxState(max, _compactSized.Max);
        return max;
    }

    private static StateId? MaxState(StateId? a, StateId? b) =>
        a is null ? b : b is null ? a : a.Value.CompareTo(b.Value) >= 0 ? a : b;

    public void SetLastCommittedStateId(in StateId stateId)
    {
        using Lock.Scope _ = _lastCommittedLock.EnterScope();
        _lastCommittedStateId = stateId;
        _hasLastCommitted = true;
    }

    public StateId? GetLastCommittedStateId()
    {
        using Lock.Scope _ = _lastCommittedLock.EnterScope();
        return _hasLastCommitted ? _lastCommittedStateId : null;
    }

    public bool RemoveAndReleaseInMemoryKnownState(in StateId stateId, SnapshotTier tier)
    {
        tier.EnsureInMemory();
        if (tier == SnapshotTier.InMemoryCompacted)
        {
            if (_compactedSnapshots.TryRemove(stateId, out Snapshot? existingState))
            {
                Interlocked.Decrement(ref _compactedSnapshotCount);
                Metrics.CompactedSnapshotCount--;

                long compactedBytes = existingState.Content.EstimateCompactedMemory();
                Metrics.CompactedSnapshotMemory -= compactedBytes;
                Metrics.TotalSnapshotMemory -= compactedBytes;

                existingState.Dispose();

                return true;
            }

            return false;
        }

        if (_snapshots.TryRemove(stateId, out Snapshot? existing))
        {
            Interlocked.Decrement(ref _snapshotCount);
            Metrics.SnapshotCount--;

            using (_sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
                sortedSnapshots.Remove(stateId);

            long totalBytes = existing.EstimateMemory();
            Metrics.SnapshotMemory -= totalBytes;
            Metrics.TotalSnapshotMemory -= totalBytes;

            existing.Dispose();

            return true;
        }

        return false;
    }

    public bool HasState(in StateId stateId)
    {
        if (_snapshots.ContainsKey(stateId)) return true;
        if (HasBaseSnapshot(stateId)) return true;
        return false;
    }

    public ArrayPoolList<StateId> GetStatesUpToBlock(long blockNumber)
    {
        if (blockNumber < 0)
            return ArrayPoolList<StateId>.Empty();

        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        return sortedSnapshots
            .GetViewBetween(new StateId(0, Hash256.Zero), new StateId(blockNumber, Keccak.MaxValue))
            .ToPooledList(0);
    }

    public void RemoveStatesUntil(long blockNumber)
    {
        using ArrayPoolList<StateId> statesUpToBlock = GetStatesUpToBlock(blockNumber);
        foreach (StateId stateToRemove in statesUpToBlock)
        {
            // A To can live in both in-memory tiers — remove from each.
            RemoveAndReleaseInMemoryKnownState(stateToRemove, SnapshotTier.InMemoryCompacted);
            RemoveAndReleaseInMemoryKnownState(stateToRemove, SnapshotTier.InMemoryBase);
        }

        // A persist also supersedes the persisted tier: drop persisted snapshots strictly below the block
        // (the base at the persisted block stays as a read/compaction source until the state advances past
        // it). Unified here so callers don't pair this with a separate persisted-tier call.
        RemovePersistedStatesUntil(blockNumber);
    }

    private const int PruneBatchSize = 1000;

    public void RemoveSiblingAndDescendents(in StateId canonicalStateId)
    {
        long canonicalBlock = canonicalStateId.BlockNumber;

        // Fast-fail when the block has no sibling in either tier: with a single state at the block,
        // everything above it chains down through the canonical one, so nothing above can be orphaned.
        // A non-canonical sibling may live in-memory or — if converted before the reorg pruned it — in
        // the persisted tier.
        if (!HasForkAt(canonicalBlock) && !HasPersistedForkAt(canonicalStateId)) return;

        // Bound the orphan walk by the highest block in either tier. GetLastSnapshotId folds in the
        // persisted tips, covering a persisted orphan above the in-memory tip (DoConvert moves a
        // converted range into the persisted tier and drops it from in-memory).
        long maxBlock = GetLastSnapshotId()?.BlockNumber ?? long.MinValue;
        if (maxBlock <= canonicalBlock) return;

        long batchStart = canonicalBlock + 1;
        int totalPruned = 0;

        while (batchStart <= maxBlock)
        {
            long batchEnd = Math.Min(batchStart + PruneBatchSize - 1, maxBlock);

            // In-memory orphans above the persisted block.
            using (ArrayPoolListRef<StateId> inMemory = GetStatesInRange(batchStart, batchEnd))
            {
                foreach (StateId stateId in inMemory)
                {
                    if (!CanReachState(stateId, canonicalStateId))
                    {
                        // A To can live in both in-memory tiers — remove from each.
                        RemoveAndReleaseInMemoryKnownState(stateId, SnapshotTier.InMemoryCompacted);
                        RemoveAndReleaseInMemoryKnownState(stateId, SnapshotTier.InMemoryBase);
                        totalPruned++;
                    }
                }
            }

            // Persisted-tier orphans above the persisted block — e.g. non-canonical siblings converted
            // into the tier (DoConvert applies no canonicality filter) before the reorg orphaned them,
            // unreachable by the in-memory pass above.
            using (ArrayPoolList<StateId> persisted = GetPersistedStatesInRange(batchStart, batchEnd))
            {
                foreach (StateId stateId in persisted)
                {
                    if (!CanReachState(stateId, canonicalStateId)
                        && RemovePersistedStateExact(stateId))
                    {
                        totalPruned++;
                    }
                }
            }

            batchStart = batchEnd + 1;
        }

        if (totalPruned > 0 && _logger.IsInfo)
        {
            _logger.Info($"Pruned {totalPruned} orphaned non-canonical snapshot(s) above persisted state {canonicalStateId}.");
        }
    }

    /// <summary>True when the persisted tier holds a non-canonical state at
    /// <paramref name="canonicalStateId"/>'s block — a fork the canonical persist orphans.</summary>
    private bool HasPersistedForkAt(in StateId canonicalStateId)
    {
        using ArrayPoolList<StateId> atBlock =
            GetPersistedStatesInRange(canonicalStateId.BlockNumber, canonicalStateId.BlockNumber);
        foreach (StateId stateId in atBlock)
            if (stateId != canonicalStateId) return true;
        return false;
    }

    /// <remarks>
    /// Walks parent (<c>From</c>) edges from <paramref name="from"/> toward <paramref name="target"/>
    /// across both tiers. Crossing into the persisted tier is required so a canonical in-memory state
    /// whose ancestry descends through a converted snapshot is not mistaken for an orphan.
    /// </remarks>
    private bool CanReachState(in StateId from, in StateId target)
    {
        if (from == target) return true;
        if (from.BlockNumber <= target.BlockNumber) return false;

        // Order-independent reachability, so a stack DFS suffices; each lease is read for its From then
        // disposed immediately. Same hardcoded in-mem-cannot-follow-persisted invariant as WalkAndAssemble.
        using PooledStack<WalkNode> stack = new();
        using PooledSet<StateId> seen = new();
        seen.Add(from);
        stack.Push(new WalkNode(from, viaPersisted: false, -1));

        // Expansion order (same as AssemblePolicy): widest skip first for the shortest reachable chain.
        ReadOnlySpan<SnapshotTier> edgePriority =
            [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase];
        while (stack.Count > 0)
        {
            WalkNode node = stack.Pop();
            foreach (SnapshotTier tier in edgePriority)
            {
                if (node.ViaPersisted && !tier.IsPersisted()) continue;

                IDisposable snapshot;
                StateId parentFrom;
                if (tier.IsPersisted())
                {
                    if (!TryLeasePersistedState(node.Current, tier, out PersistedSnapshot? persisted)) continue;
                    (snapshot, parentFrom) = (persisted, persisted.From);
                }
                else
                {
                    if (!TryLeaseInMemoryState(node.Current, tier, out Snapshot? inMemory)) continue;
                    (snapshot, parentFrom) = (inMemory, inMemory.From);
                }

                snapshot.Dispose();

                if (parentFrom == target) return true;
                if (parentFrom.BlockNumber > target.BlockNumber && seen.Add(parentFrom))
                    stack.Push(new WalkNode(parentFrom, tier.IsPersisted(), -1));
            }
        }
        return false;
    }

    private ArrayPoolListRef<StateId> GetStatesInRange(long blockStartInclusive, long blockEndInclusive)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        SortedSet<StateId> view = sortedSnapshots.GetViewBetween(
            new StateId(blockStartInclusive, Hash256.Zero),
            new StateId(blockEndInclusive, Keccak.MaxValue));

        ArrayPoolListRef<StateId> result = new(view.Count);
        foreach (StateId stateId in view) result.Add(stateId);
        return result;
    }

    // ===================== Persisted tier =====================

    /// <summary>
    /// Index a caller-built <paramref name="snapshot"/> into the bucket for <paramref name="tier"/>,
    /// acquiring the bucket's lease under its lock so a racing prune can't dispose it mid-insert. The
    /// caller retains and disposes its construction lease, and owns the catalog entry — a freshly
    /// persisted/compacted snapshot writes one; a snapshot reloaded from the catalog does not.
    /// </summary>
    public void AddPersistedSnapshot(PersistedSnapshot snapshot, SnapshotTier tier)
    {
        if (_logger.IsDebug) _logger.Debug($"Created persisted snapshot {tier} {snapshot.From.BlockNumber}->{snapshot.To.BlockNumber} ({snapshot.Size} bytes)");
        BucketFor(tier).Add(snapshot.To, snapshot);
    }

    /// <inheritdoc />
    public bool ReplacePersistedSnapshot(in StateId to, PersistedSnapshot replacement, SnapshotTier tier) =>
        BucketFor(tier).Replace(to, replacement);

    /// <summary>
    /// Lease the persisted snapshot ending at <paramref name="toState"/> from the bucket for
    /// <paramref name="tier"/> (must be a <c>Persisted*</c> value). Caller disposes the lease.
    /// </summary>
    public bool TryLeasePersistedState(in StateId toState, SnapshotTier tier, [NotNullWhen(true)] out PersistedSnapshot? snapshot) => tier switch
    {
        SnapshotTier.PersistedBase => TryLeaseFrom(_base, toState, out snapshot),
        SnapshotTier.PersistedSmallCompacted => TryLeaseFrom(_smallCompacted, toState, out snapshot),
        SnapshotTier.PersistedLargeCompacted => TryLeaseFrom(_largeCompacted, toState, out snapshot),
        SnapshotTier.PersistedCompactSized => TryLeaseFrom(_compactSized, toState, out snapshot),
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Only persisted tiers are valid here."),
    };

    private static bool TryLeaseFrom(PersistedSnapshotBucket bucket, in StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (bucket.TryGet(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        snapshot = null;
        return false;
    }

    /// <summary>The bucket for a persisted tier — a 1:1 map.</summary>
    private PersistedSnapshotBucket BucketFor(SnapshotTier tier) => tier switch
    {
        SnapshotTier.PersistedBase => _base,
        SnapshotTier.PersistedSmallCompacted => _smallCompacted,
        SnapshotTier.PersistedLargeCompacted => _largeCompacted,
        SnapshotTier.PersistedCompactSized => _compactSized,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Only persisted tiers are valid here."),
    };

    /// <summary>
    /// Lease every base snapshot tiling <c>(from, to]</c>, walking <c>From</c> pointers back from
    /// <paramref name="to"/>. Bulk-prefetches the base blob-RLP regions before a linked CompactSized is
    /// scanned. Best-effort — stops at the first gap. Caller disposes the returned list.
    /// </summary>
    public PersistedSnapshotList LeaseBaseSnapshotsInRange(StateId from, StateId to)
    {
        PersistedSnapshotList result = new(0);
        StateId current = to;
        while (current != from && current.BlockNumber > from.BlockNumber)
        {
            if (!_base.TryGet(current, out PersistedSnapshot? snapshot) || !snapshot.TryAcquire())
                break;
            result.Add(snapshot);
            if (snapshot.From == current)
                break; // self-loop guard
            current = snapshot.From;
        }
        return result;
    }

    /// <summary>
    /// Prune persisted snapshots with To.BlockNumber before the given block. Blob arenas referenced by
    /// surviving compacted snapshots stay alive via the <see cref="BlobArenaManager"/> refcount — no
    /// explicit "referenced base id" check is needed here.
    /// </summary>
    public void RemovePersistedStatesUntil(long blockNumber)
    {
        _base.PruneBefore(blockNumber);
        _smallCompacted.PruneBefore(blockNumber);
        _largeCompacted.PruneBefore(blockNumber);
        _compactSized.PruneBefore(blockNumber);
    }

    /// <summary>
    /// Enumerate persisted <c>To</c>-StateIds across all buckets whose <c>To.BlockNumber</c> is in
    /// <c>[startBlockInclusive, endBlockInclusive]</c>, deduped. Caller disposes the returned list.
    /// </summary>
    private ArrayPoolList<StateId> GetPersistedStatesInRange(long startBlockInclusive, long endBlockInclusive)
    {
        if (endBlockInclusive < startBlockInclusive) return ArrayPoolList<StateId>.Empty();

        StateId min = new(startBlockInclusive, ValueKeccak.Zero);
        StateId max = new(endBlockInclusive, ValueKeccak.MaxValue);

        // A `To` can live in more than one bucket, so dedupe across the block-ordered sets.
        HashSet<StateId> union = [];
        _base.CollectRange(min, max, union);
        _smallCompacted.CollectRange(min, max, union);
        _largeCompacted.CollectRange(min, max, union);
        _compactSized.CollectRange(min, max, union);

        ArrayPoolList<StateId> result = new(union.Count);
        foreach (StateId to in union) result.Add(to);
        return result;
    }

    /// <summary>
    /// Remove the persisted snapshot(s) at exactly <paramref name="toState"/> from every bucket it
    /// appears in, releasing their leases. Returns <c>true</c> when anything was removed.
    /// </summary>
    // `|` (not `||`): every bucket must be attempted — a `To` can appear in more than one.
    public bool RemovePersistedStateExact(in StateId toState) =>
        _base.RemoveExact(toState) | _smallCompacted.RemoveExact(toState) | _largeCompacted.RemoveExact(toState) | _compactSized.RemoveExact(toState);

    public bool HasBaseSnapshot(in StateId stateId) => _base.ContainsKey(stateId);

    public IEnumerable<PersistedSnapshot> PersistedSnapshots
    {
        get
        {
            foreach (PersistedSnapshot snap in _base.Snapshots) yield return snap;
            foreach (PersistedSnapshot snap in _smallCompacted.Snapshots) yield return snap;
            foreach (PersistedSnapshot snap in _largeCompacted.Snapshots) yield return snap;
            foreach (PersistedSnapshot snap in _compactSized.Snapshots) yield return snap;
        }
    }

    public void MarkPersistedTierForShutdown()
    {
        // Mark every loaded snapshot's files as shutdown-preserved before any teardown. Snapshots
        // pruned earlier this session aren't in the buckets, so their files won't get the flag and are
        // deleted when the arena/blob managers are disposed. Must complete for every bucket before
        // Dispose tears any bucket down — a file shared between a base and a compacted snapshot must be
        // flagged before either is disposed.
        _base.PersistAllOnShutdown();
        _smallCompacted.PersistAllOnShutdown();
        _largeCompacted.PersistAllOnShutdown();
        _compactSized.PersistAllOnShutdown();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Dispose snapshots (drops reservation + blob leases) and roll back each bucket's metrics share.
        // Files self-clean as their refcount hits zero; the preserve flag from MarkPersistedTierForShutdown
        // keeps the on-disk file for opt-in snapshots.
        _base.DisposeAndClear();
        _smallCompacted.DisposeAndClear();
        _largeCompacted.DisposeAndClear();
        _compactSized.DisposeAndClear();
    }

    // ---- Backward-walk infrastructure ----
    // Per-edge policies and the shared chain-gathering driver for the Assemble* / CanReach /
    // FindSnapshotToPersist walks above; grouped here to keep the public surface uncluttered. Each policy
    // inlines its own edge-priority order. The driver hardcodes the invariant that once an edge crosses
    // into the persisted tier the in-memory tiers are unreachable.

    private readonly struct WalkNode(in StateId current, bool viaPersisted, int parentIndex)
    {
        public readonly StateId Current = current;
        public readonly bool ViaPersisted = viaPersisted;
        public readonly int ParentIndex = parentIndex;
    }

    /// <summary>Per-edge verdict returned by <see cref="IAssemblePolicy.Decide"/>.</summary>
    private enum AssembleStep
    {
        /// <summary>Drop this edge — don't traverse it or count it as a winner.</summary>
        Skip,
        /// <summary>Follow the edge and keep searching; not a winner.</summary>
        Traverse,
        /// <summary>Mark current best winner but keep walking — a deeper edge may still win. The last
        /// <see cref="Win"/> before the frontier drains is the final winner.</summary>
        Win,
        /// <summary>Mark the winner and stop immediately.</summary>
        WinAndStop,
    }

    /// <summary>
    /// Per-edge policy for <see cref="WalkAndAssemble{TPolicy}"/>: the edge-priority table and a per-edge
    /// <see cref="Decide"/> verdict. The driver owns storage, lease handling, cycle detection, winner
    /// tracking, and reconstruction; the policy only inspects each candidate parent edge.
    /// </summary>
    private interface IAssemblePolicy
    {
        ReadOnlySpan<SnapshotTier> EdgePriority { get; }
        /// <summary>Verdict for one parent edge: <paramref name="to"/> is the node being expanded,
        /// <paramref name="from"/> is the parent it reaches over <paramref name="tier"/>.</summary>
        AssembleStep Decide(in StateId to, in StateId from, SnapshotTier tier);
    }

    // Full dual-tier walk for AssembleSnapshots. The driver enforces the in-mem-cannot-follow-persisted
    // invariant, so this only filters by block: an overshooting persisted snapshot is the terminal
    // element, an overshooting in-memory edge is unusable, and reaching the target exactly wins (a
    // different state at the target's block is a sibling fork, skipped).
    private readonly struct AssemblePolicy(StateId target) : IAssemblePolicy
    {
        // Expansion order, widest skip first, so a read assembles the shortest chain: large-compacted
        // (>CompactSize), CompactSized, in-memory hops, then narrow small-compacted and persisted bases.
        public ReadOnlySpan<SnapshotTier> EdgePriority =>
            [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase];

        public AssembleStep Decide(in StateId to, in StateId from, SnapshotTier tier)
        {
            if (from.BlockNumber < target.BlockNumber)
                return tier.IsPersisted() ? AssembleStep.WinAndStop : AssembleStep.Skip;
            if (from == target) return AssembleStep.WinAndStop;
            // A different state at the target's block is a sibling fork — don't win there.
            return from.BlockNumber == target.BlockNumber ? AssembleStep.Skip : AssembleStep.Traverse;
        }
    }

    // In-memory-only walk for AssembleInMemorySnapshotsForCompaction: widest-jump first, pruning edges
    // below minBlockNumber, winning at the first node reaching it.
    private readonly struct InMemoryCompactionPolicy(long minBlockNumber) : IAssemblePolicy
    {
        public ReadOnlySpan<SnapshotTier> EdgePriority => [SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase];

        public AssembleStep Decide(in StateId to, in StateId from, SnapshotTier tier) =>
            from.BlockNumber < minBlockNumber ? AssembleStep.Skip
            : from.BlockNumber == minBlockNumber ? AssembleStep.WinAndStop
            : AssembleStep.Traverse;
    }

    // Best-effort persisted-only compaction walk: prunes edges overshooting minBlockNumber and wins on
    // the deepest (lowest-block) node reached. Widest-first + BFS gives the widest path to each depth;
    // the window need not be fully populated.
    private struct PersistedCompactionPolicy(long minBlockNumber) : IAssemblePolicy
    {
        private long _winnerBlock = long.MaxValue;

        public readonly ReadOnlySpan<SnapshotTier> EdgePriority =>
            [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase];

        public AssembleStep Decide(in StateId to, in StateId from, SnapshotTier tier)
        {
            if (from.BlockNumber < minBlockNumber) return AssembleStep.Skip;
            if (from.BlockNumber == minBlockNumber) return AssembleStep.WinAndStop; // window start, deepest possible
            if (from.BlockNumber < _winnerBlock)
            {
                _winnerBlock = from.BlockNumber;
                return AssembleStep.Win;
            }
            return AssembleStep.Traverse;
        }
    }

    // FindSnapshotToPersist navigation: walk From-edges down to currentPersistedState, winning at the first
    // edge reaching it that spans at most CompactSize. The >CompactSize large-compacted is a navigation-only
    // skip-pointer (followed above the target, never won onto it). Dedup runs only on retained edges, so a
    // skipped edge can't shadow the real candidate edge to the same target.
    private readonly struct FindPersistPolicy(StateId currentPersistedState, int compactSize) : IAssemblePolicy
    {
        // LargeCompacted (>CompactSize) leads as a navigation-only skip-pointer; the rest are candidates,
        // CompactSized (the ==CompactSize boundary unit) first.
        public ReadOnlySpan<SnapshotTier> EdgePriority =>
            [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.InMemoryCompacted, SnapshotTier.PersistedSmallCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedBase];

        public AssembleStep Decide(in StateId to, in StateId from, SnapshotTier tier)
        {
            if (from == currentPersistedState)
                // Any chunk spanning at most CompactSize is persistable; a wider large-compacted is skip-only.
                return to.BlockNumber - from.BlockNumber <= compactSize ? AssembleStep.WinAndStop : AssembleStep.Skip;
            return from.BlockNumber > currentPersistedState.BlockNumber ? AssembleStep.Traverse : AssembleStep.Skip;
        }
    }

    /// <summary>
    /// Backward BFS over parent (<c>From</c>) edges, gathering the winning chain into an
    /// <see cref="AssembledSnapshotResult"/> (in-memory + persisted lists, oldest-first). Owns the
    /// frontier queue, visited buffer, cycle detection, winner tracking, and reconstruction. Hardcodes
    /// the invariant that once an edge crosses into the persisted tier the in-memory tiers are
    /// unreachable. The <paramref name="policy"/> supplies the edge-priority table and per-edge verdict.
    /// </summary>
    private AssembledSnapshotResult WalkAndAssemble<TPolicy>(in StateId start, int estimatedSize, ref TPolicy policy)
        where TPolicy : struct, IAssemblePolicy
    {
        using PooledQueue<WalkNode> queue = new();
        using PooledSet<StateId> seen = new();
        // visited owns a lease on every retained edge; GatherChain re-leases the winning path before the
        // finally releases all of them.
        ArrayPoolList<(IDisposable snapshot, int parentIndex)> visited = new(estimatedSize);
        try
        {
            int winnerIndex = -1;
            seen.Add(start);
            // Root starts in-memory; ViaPersisted flips on as the walk crosses a persisted edge. A
            // persisted-only policy simply has no in-memory tiers to expand.
            queue.Enqueue(new WalkNode(start, viaPersisted: false, -1));

            while (queue.Count > 0)
            {
                WalkNode node = queue.Dequeue();

                foreach (SnapshotTier tier in policy.EdgePriority)
                {
                    // Invariant: a node reached over a persisted edge chains only to persisted tiers.
                    if (node.ViaPersisted && !tier.IsPersisted()) continue;

                    IDisposable snapshot;
                    StateId from;
                    if (tier.IsPersisted())
                    {
                        if (!TryLeasePersistedState(node.Current, tier, out PersistedSnapshot? persisted)) continue;
                        (snapshot, from) = (persisted, persisted.From);
                    }
                    else
                    {
                        if (!TryLeaseInMemoryState(node.Current, tier, out Snapshot? inMemory)) continue;
                        (snapshot, from) = (inMemory, inMemory.From);
                    }

                    AssembleStep step = policy.Decide(node.Current, from, tier);
                    if (step == AssembleStep.Skip) { snapshot.Dispose(); continue; }
                    // Cycle detection — dedup AFTER Decide so a skipped (non-candidate) edge doesn't claim
                    // its target and shadow a later candidate edge to the same node. No-op for policies
                    // whose verdict is constant per node.
                    if (!seen.Add(from)) { snapshot.Dispose(); continue; }

                    int idx = visited.Count;
                    visited.Add((snapshot, node.ParentIndex));
                    if (step != AssembleStep.Traverse) winnerIndex = idx; // Win or WinAndStop
                    if (step == AssembleStep.WinAndStop) return GatherChain(visited, winnerIndex, estimatedSize);

                    queue.Enqueue(new WalkNode(from, tier.IsPersisted(), idx));
                }
            }

            return GatherChain(visited, winnerIndex, estimatedSize);
        }
        finally
        {
            for (int i = 0; i < visited.Count; i++) visited[i].snapshot.Dispose();
            visited.Dispose();
        }
    }

    /// <summary>
    /// Reconstruct the winner→root path into oldest-first in-memory + persisted lists, re-leasing each
    /// snapshot so it survives the caller's release of the visited buffer. The winner is the terminus
    /// (oldest), and the in-mem-before-persisted invariant keeps each tier contiguous, so both lists come
    /// out ascending without a reversal. Empty lists when no winner was found.
    /// </summary>
    private static AssembledSnapshotResult GatherChain(
        ArrayPoolList<(IDisposable snapshot, int parentIndex)> visited, int winnerIndex, int estimatedSize)
    {
        if (winnerIndex < 0)
            return new AssembledSnapshotResult(SnapshotPooledList.Empty(), PersistedSnapshotList.Empty());

        SnapshotPooledList inMemory = new(estimatedSize);
        PersistedSnapshotList persisted = new(estimatedSize);
        for (int walk = winnerIndex; walk >= 0; walk = visited[walk].parentIndex)
        {
            switch (visited[walk].snapshot)
            {
                case PersistedSnapshot ps:
                    // visited still holds a lease, so re-acquire cannot fail.
                    bool pAcquired = ps.TryAcquire();
                    Debug.Assert(pAcquired, "TryAcquire failed despite held lease");
                    persisted.Add(ps);
                    break;
                case Snapshot s:
                    bool sAcquired = s.TryAcquire();
                    Debug.Assert(sAcquired, "TryAcquire failed despite held lease");
                    inMemory.Add(s);
                    break;
            }
        }
        return new AssembledSnapshotResult(inMemory, persisted);
    }
}
