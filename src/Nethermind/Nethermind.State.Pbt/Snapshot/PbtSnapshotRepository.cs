// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Api.Monitoring;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>
/// Holds the in-memory diff layers keyed by their <see cref="PbtSnapshot.To"/> state, including
/// fork siblings, and assembles backward chains for bundle construction.
/// </summary>
/// <remarks>
/// Memory base and compacted layers coexist with four persisted tiers. Compacted edges are traversal
/// shortcuts, not substitutes for independently available base snapshots.
/// </remarks>
public class PbtSnapshotRepository(IMetricsConfig metricsConfig, ISnapshotCatalog catalog, PbtPersistedPublicationGate publicationGate) : IDisposable
{
    // Metrics are optional; operational memory accounting must remain active for byte-budget enforcement.
    private readonly bool _recordDetailedMetrics = metricsConfig.EnableDetailedMetric;
    private readonly Lock _lock = new();
    private readonly Dictionary<StateId, PbtSnapshot> _snapshots = [];
    private readonly Dictionary<StateId, PbtSnapshot> _compactedSnapshots = [];
    private StateId? _lastCommittedStateId;
    private readonly PbtPersistedIndex _persisted = new(Comparer<StateId>.Create(CompareStates));
    private readonly ISnapshotCatalog _catalog = catalog;
    private readonly PbtPersistedPublicationGate _publicationGate = publicationGate;
    private long _memoryBytes;
    private bool _disposed;

    public long InMemorySnapshotBytes { get { lock (_lock) return _memoryBytes; } }
    public int PersistedCount { get { lock (_lock) return _persisted.Count; } }
    private static long Bytes(PbtSnapshot snapshot) => snapshot.PayloadSize.Leaf + snapshot.PayloadSize.Node;


    public int Count
    {
        get
        {
            lock (_lock) return _snapshots.Count;
        }
    }

    public int CompactedCount
    {
        get
        {
            lock (_lock) return _compactedSnapshots.Count;
        }
    }

    public StateId? GetLastCommittedStateId()
    {
        lock (_lock) return _lastCommittedStateId;
    }

    /// <summary>Adds a sealed base layer, taking ownership of one lease. Returns false (and releases) on duplicate.</summary>
    public bool TryAdd(PbtSnapshot snapshot)
    {
        PbtSnapshotPayloadSize payloadSize = snapshot.PayloadSize;
        lock (_lock)
        {
            if (!_disposed && _snapshots.TryAdd(snapshot.To, snapshot))
            {
                _memoryBytes += Bytes(snapshot);
                _lastCommittedStateId = snapshot.To;
                Metrics.AddPbtBaseSnapshotCount(1);
                if (_recordDetailedMetrics) Metrics.AddPbtBaseSnapshotMemory(payloadSize, 1);
                return true;
            }
        }

        snapshot.Dispose();
        return false;
    }

    /// <summary>Publishes a compacted layer alongside the base layer at the same state, taking ownership of one lease.</summary>
    /// <remarks>Returns false (and releases) when that state already has one — two compactions raced.</remarks>
    public bool TryAddCompacted(PbtSnapshot snapshot)
    {
        lock (_lock)
        {
            if (!_disposed && _compactedSnapshots.TryAdd(snapshot.To, snapshot)) { _memoryBytes += Bytes(snapshot); return true; }
        }

        snapshot.Dispose();
        return false;
    }

    /// <summary>Releases orphan descendants above the successful persistence boundary while preserving leased readers.</summary>
    /// <remarks>Call before removing states at the boundary, whose siblings identify the forks to prune.</remarks>
    public void RemoveSiblingAndDescendents(in StateId canonicalState)
    {
        List<StateId> abandoned = [];
        List<PbtSnapshot> memoryCandidates = [];
        List<(StateId To, SnapshotTier Tier)> persisted = [];
        lock (_publicationGate.Sync)
        {
            lock (_lock)
            {
                HashSet<StateId> states = [.. _snapshots.Keys, .. _compactedSnapshots.Keys];
                foreach (PbtPersistedSnapshot snapshot in _persisted.Snapshots) states.Add(snapshot.To);
                bool hasSibling = false;
                foreach (StateId state in states) hasSibling |= state.BlockNumber == canonicalState.BlockNumber && state != canonicalState;
                if (!hasSibling) return;
                List<StateId> ordered = [.. states];
                ordered.Sort(CompareStates);
                HashSet<StateId> reachable = [canonicalState];
                // A persisted descendant cannot rely on memory below it; backward reads cannot return to memory.
                HashSet<StateId> persistedReachable = [canonicalState];
                foreach (StateId state in ordered)
                {
                    if (Height(state) <= Height(canonicalState)) continue;
                    bool connected = _snapshots.TryGetValue(state, out PbtSnapshot? memory) && reachable.Contains(memory.From)
                        || _compactedSnapshots.TryGetValue(state, out memory) && reachable.Contains(memory.From);
                    bool persistedConnected = false;
                    foreach (SnapshotTier tier in PersistedPriority)
                        if (_persisted.TryGet(state, tier, out PbtPersistedSnapshot? snapshot)) persistedConnected |= persistedReachable.Contains(snapshot.From);
                    if (persistedConnected) persistedReachable.Add(state);
                    if (connected || persistedConnected) reachable.Add(state);
                    else abandoned.Add(state);
                }
                foreach (StateId state in abandoned)
                {
                    if (_snapshots.TryGetValue(state, out PbtSnapshot? snapshot)) memoryCandidates.Add(snapshot);
                    if (_compactedSnapshots.TryGetValue(state, out snapshot)) memoryCandidates.Add(snapshot);
                }
                HashSet<StateId> abandonedSet = [.. abandoned];
                foreach (PbtPersistedSnapshot snapshot in _persisted.Snapshots)
                    if (abandonedSet.Contains(snapshot.To)) persisted.Add((snapshot.To, snapshot.Tier));
            }
            foreach (PbtSnapshot snapshot in memoryCandidates) RemoveMemorySource(snapshot);
            foreach ((StateId to, SnapshotTier tier) in persisted) RemovePersistedExact(to, tier);
        }
    }

    public bool HasState(in StateId stateId)
    {
        lock (_lock) return _snapshots.ContainsKey(stateId) || HasPersistedBase(stateId);
    }

    /// <summary>
    /// Leases the layers covering <paramref name="head"/> back to <paramref name="minBlockNumber"/>,
    /// oldest first, for a compaction window. Returns false when the window cannot be assembled.
    /// </summary>
    /// <remarks>
    /// The floor is a height rather than a state, because a window is defined by the schedule's block
    /// alignment and not by what happens to be persisted. It may sit below genesis, which is why it is
    /// signed: an early wide window simply cannot be assembled rather than wrapping into a huge one.
    /// </remarks>
    public bool TryLeaseCompactionWindow(in StateId head, long minBlockNumber, PbtSnapshotPooledList chain)
    {
        using PbtAssembledSnapshotResult leased = Walk(head, MemoryPriority, (_, from, _) =>
            Height(from) < minBlockNumber ? Step.Skip : Height(from) == minBlockNumber ? Step.Stop : Step.Traverse);
        if (leased.SnapshotCount == 0) return false;
        foreach (PbtSnapshot snapshot in leased.InMemory)
        {
            if (!snapshot.TryLease()) throw new InvalidOperationException("Held snapshot lease expired.");
            chain.Add(snapshot);
        }
        return true;
    }

    /// <summary>Removes and releases every layer, of either tier, at or below <paramref name="blockNumber"/> — persisted canonical layers and stale fork siblings alike.</summary>
    public void RemoveStatesUntil(ulong blockNumber)
    {
        List<PbtSnapshot> removed = [];
        lock (_lock)
        {
            int firstCompacted = Collect(_snapshots, removed, static (id, floor) => id.BlockNumber <= floor, blockNumber);
            Metrics.AddPbtBaseSnapshotCount(-firstCompacted);
            if (_recordDetailedMetrics)
                for (int i = 0; i < firstCompacted; i++) Metrics.AddPbtBaseSnapshotMemory(removed[i].PayloadSize, -1);

            Collect(_compactedSnapshots, removed, static (id, floor) => id.BlockNumber <= floor, blockNumber);
            foreach (PbtSnapshot snapshot in removed) _memoryBytes -= Bytes(snapshot);
        }

        foreach (PbtSnapshot snapshot in removed)
        {
            snapshot.Dispose();
        }
    }

    /// <summary>Removes and releases the compacted layers at exactly <paramref name="blockNumber"/>, leaving the base tier alone.</summary>
    /// <remarks>
    /// A compacted layer is superseded once a wider one spans across it: it costs memory while no walk
    /// would ever prefer it again.
    /// </remarks>
    public void RemoveCompactedAt(ulong blockNumber)
    {
        List<PbtSnapshot> removed = [];
        lock (_lock)
        {
            Collect(_compactedSnapshots, removed, static (id, at) => id.BlockNumber == at, blockNumber);
            foreach (PbtSnapshot snapshot in removed) _memoryBytes -= Bytes(snapshot);
        }

        foreach (PbtSnapshot snapshot in removed)
        {
            snapshot.Dispose();
        }
    }

    private static int Collect(Dictionary<StateId, PbtSnapshot> tier, List<PbtSnapshot> removed, Func<StateId, ulong, bool> matches, ulong blockNumber)
    {
        int first = removed.Count;
        foreach ((StateId stateId, PbtSnapshot snapshot) in tier)
        {
            if (matches(stateId, blockNumber)) removed.Add(snapshot);
        }

        for (int i = first; i < removed.Count; i++)
        {
            tier.Remove(removed[i].To);
        }

        return removed.Count;
    }

    /// <summary>The state's height as a signed number, so <see cref="StateId.PreGenesis"/> (top of the unsigned range) reinterprets to -1 and orders below block 0.</summary>
    private static long Height(in StateId stateId) => (long)stateId.BlockNumber;

    private bool CanReachState(in StateId head, in StateId target)
    {
        if (head == target) return true;
        StateId wanted = target;
        using PbtAssembledSnapshotResult assembled = Walk(head, ReadPriority, (_, from, _) =>
            from == wanted ? Step.Stop : Height(from) > Height(wanted) ? Step.Traverse : Step.Skip);
        return assembled.SnapshotCount > 0;
    }

    private ArrayPoolList<StateId> GetPersistedStatesInRange(ulong first, ulong last)
    {
        ArrayPoolList<StateId> states = new(16);
        lock (_lock)
            foreach (StateId state in _persisted.StatesBetween(new(first, default), new(last, ValueKeccak.MaxValue)))
                states.Add(state);
        return states;
    }

    private void RemovePersistedState(in StateId state)
    {
        List<SnapshotTier> tiers = [];
        lock (_lock)
            foreach (SnapshotTier tier in PersistedPriority)
                if (_persisted.Contains(state, tier)) tiers.Add(tier);
        foreach (SnapshotTier tier in tiers) RemovePersistedExact(state, tier);
    }

    public void RemoveFinalizedPersistedForks(in StateId persisted, IStateHeaderProvider provider)
    {
        lock (_publicationGate.Sync)
        {
            StateId? committed = GetLastCommittedStateId();
            ulong finalized = provider.FinalizedBlockNumber;
            if (committed is null || committed == StateId.PreGenesis || finalized > committed.Value.BlockNumber) return;
            StateId head = committed.Value;
            bool ancestryVerified = false;
            ulong first = persisted == StateId.PreGenesis ? 0 : persisted.BlockNumber;
            const ulong batchSize = 4096;
            while (first <= finalized)
            {
                ulong last = finalized - first >= batchSize - 1 ? first + batchSize - 1 : finalized;
                using ArrayPoolList<StateId> states = GetPersistedStatesInRange(first, last);
                foreach (StateId state in states)
                {
                    if (state == persisted) continue;
                    Hash256? root = provider.GetFinalizedHeader(state.BlockNumber)?.StateRoot;
                    if (root is null || state.StateRoot == root.ValueHash256) continue;
                    if (!ancestryVerified)
                    {
                        Hash256? anchor = provider.GetFinalizedHeader(finalized)?.StateRoot;
                        if (anchor is not null && !CanReachState(head, new StateId(finalized, anchor))) return;
                        if (persisted != StateId.PreGenesis && !CanReachState(head, persisted)) return;
                        ancestryVerified = true;
                    }
                    if (GetLastCommittedStateId() != committed) return;
                    if (CanReachState(head, state)) continue;
                    RemovePersistedState(state);
                }
                if (last == finalized) break;
                first = last + 1;
            }
        }
    }

    private static readonly SnapshotTier[] ReadPriority = [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase];
    private static readonly SnapshotTier[] PersistPriority = [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.InMemoryCompacted, SnapshotTier.PersistedSmallCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedBase];
    private static readonly SnapshotTier[] PersistedPriority = [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase];
    private static readonly SnapshotTier[] MemoryPriority = [SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase];
    private enum Step { Skip, Traverse, Win, Stop }

    /// <summary>Indexes <paramref name="snapshot"/>, releasing the one it displaces at the same state and tier.</summary>
    public bool TryAddPersisted(PbtPersistedSnapshot snapshot)
    {
        PbtPersistedSnapshot? displaced;
        lock (_publicationGate.Sync)
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_persisted.TryAdd(snapshot, out displaced)) return false;
            }
        displaced?.Dispose();
        return true;
    }

    public bool ContainsMemorySource(PbtSnapshot snapshot)
    {
        lock (_lock) return _snapshots.TryGetValue(snapshot.To, out PbtSnapshot? value) && ReferenceEquals(snapshot, value)
            || _compactedSnapshots.TryGetValue(snapshot.To, out value) && ReferenceEquals(snapshot, value);
    }

    public bool TryLeaseMemoryState(in StateId state, SnapshotTier tier, out PbtSnapshot? snapshot)
    {
        tier.EnsureInMemory();
        lock (_lock)
        {
            Dictionary<StateId, PbtSnapshot> source = tier == SnapshotTier.InMemoryBase ? _snapshots : _compactedSnapshots;
            if (source.TryGetValue(state, out snapshot) && snapshot.TryLease()) return true;
            snapshot = null;
            return false;
        }
    }

    public bool TryLeasePersistedCatalogKey(in StateId to, SnapshotTier tier, out PbtPersistedSnapshot? snapshot)
    {
        lock (_lock)
        {
            if (_persisted.TryGet(to, tier, out snapshot) && snapshot.TryLease()) return true;
            snapshot = null;
            return false;
        }
    }

    private bool HasPersistedBase(in StateId state) => _persisted.Contains(state, SnapshotTier.PersistedBase);

    public PbtAssembledSnapshotResult TryLeaseReadChain(in StateId target, in StateId readerFloor)
    {
        if (target == readerFloor) return Empty();
        StateId floor = readerFloor;
        return Walk(target, ReadPriority, (_, from, tier) => Height(from) < Height(floor)
            ? tier.IsPersisted() ? Step.Stop : Step.Skip
            : from == floor ? Step.Stop : from.BlockNumber == floor.BlockNumber ? Step.Skip : Step.Traverse);
    }

    public PbtAssembledSnapshotResult TryLeasePersistedChain(in StateId target, ulong minimumBlockNumber)
    {
        long best = long.MaxValue;
        return Walk(target, PersistedPriority, (_, from, _) =>
        {
            if (Height(from) < (long)minimumBlockNumber) return Step.Skip;
            if (from.BlockNumber == minimumBlockNumber) return Step.Stop;
            if (Height(from) < best) { best = Height(from); return Step.Win; }
            return Step.Traverse;
        });
    }

    /// <summary>Leases the next snapshot to persist, the one extending <paramref name="baseState"/>; at most one of the pair is set.</summary>
    public (PbtPersistedSnapshot? Persisted, PbtSnapshot? InMemory) FindCandidateToPersist(in StateId seed, in StateId baseState, ulong compactSize,
        Func<StateId, bool> acceptsFinalizedRoot)
    {
        StateId floor = baseState;
        using PbtAssembledSnapshotResult assembled = Walk(seed, PersistPriority, (to, from, _) =>
            from == floor ? unchecked(to.BlockNumber - from.BlockNumber) <= compactSize && acceptsFinalizedRoot(to) ? Step.Stop : Step.Skip
            : Height(from) > Height(floor) ? Step.Traverse : Step.Skip);
        // The candidate is the oldest layer, and persisted layers are always older than in-memory ones.
        if (assembled.Persisted.Count > 0)
        {
            PbtPersistedSnapshot persisted = assembled.Persisted[0];
            if (!persisted.TryLease()) throw new InvalidOperationException("Held snapshot lease expired.");
            return (persisted, null);
        }
        if (assembled.InMemory.Count > 0)
        {
            PbtSnapshot memory = assembled.InMemory[0];
            if (!memory.TryLease()) throw new InvalidOperationException("Held snapshot lease expired.");
            return (null, memory);
        }
        return (null, null);
    }

    /// <param name="decide">Judges an edge from its <c>To</c> state, its <c>From</c> state and its tier.</param>
    private PbtAssembledSnapshotResult Walk(in StateId start, SnapshotTier[] priority, Func<StateId, StateId, SnapshotTier, Step> decide)
    {
        List<(IDisposable Snapshot, int Parent)> visited = [];
        Queue<(StateId State, bool Persisted, int Parent)> queue = new();
        HashSet<StateId> seen = [start];
        queue.Enqueue((start, false, -1));
        int winner = -1;
        try
        {
            while (queue.TryDequeue(out (StateId State, bool Persisted, int Parent) node))
            {
                foreach (SnapshotTier tier in priority)
                {
                    if (node.Persisted && !tier.IsPersisted()) continue;
                    if (!TryLeaseEdge(node.State, tier, out IDisposable? edge, out StateId from)) continue;
                    Step step;
                    try { step = decide(node.State, from, tier); }
                    catch { edge.Dispose(); throw; }
                    if (step == Step.Skip || !seen.Add(from)) { edge.Dispose(); continue; }
                    int index = visited.Count;
                    visited.Add((edge, node.Parent));
                    if (step is Step.Win or Step.Stop) winner = index;
                    if (step == Step.Stop) return Gather(visited, winner);
                    queue.Enqueue((from, tier.IsPersisted(), index));
                }
            }
            return winner < 0 ? Empty() : Gather(visited, winner);
        }
        finally { foreach ((IDisposable edge, _) in visited) edge.Dispose(); }
    }

    private bool TryLeaseEdge(in StateId state, SnapshotTier tier, [NotNullWhen(true)] out IDisposable? edge, out StateId from)
    {
        lock (_lock)
        {
            if (!tier.IsPersisted())
            {
                Dictionary<StateId, PbtSnapshot> source = tier == SnapshotTier.InMemoryBase ? _snapshots : _compactedSnapshots;
                if (source.TryGetValue(state, out PbtSnapshot? snapshot) && snapshot.TryLease())
                {
                    (edge, from) = (snapshot, snapshot.From);
                    return true;
                }
            }
            else if (_persisted.TryGet(state, tier, out PbtPersistedSnapshot? snapshot) && snapshot.TryLease())
            {
                (edge, from) = (snapshot, snapshot.From);
                return true;
            }
            (edge, from) = (null, default);
            return false;
        }
    }

    private static PbtAssembledSnapshotResult Empty() => new(new PbtSnapshotPooledList(0), new PbtPersistedSnapshotList(0));

    private static PbtAssembledSnapshotResult Gather(List<(IDisposable Snapshot, int Parent)> visited, int winner)
    {
        PbtAssembledSnapshotResult assembled = Empty();
        try
        {
            for (int i = winner; i >= 0; i = visited[i].Parent)
            {
                switch (visited[i].Snapshot)
                {
                    case PbtSnapshot memory:
                        if (!memory.TryLease()) throw new InvalidOperationException("Held snapshot lease expired.");
                        assembled.InMemory.Add(memory);
                        break;
                    case PbtPersistedSnapshot persisted:
                        if (!persisted.TryLease()) throw new InvalidOperationException("Held snapshot lease expired.");
                        assembled.Persisted.Add(persisted);
                        break;
                }
            }
            return assembled;
        }
        catch { assembled.Dispose(); throw; }
    }

    private bool RemovePersistedExact(in StateId to, SnapshotTier tier)
    {
        PbtPersistedSnapshot? removed;
        lock (_publicationGate.Sync)
        {
            lock (_lock)
                if (!_persisted.TryGet(to, tier, out removed)) return false;
            _catalog.Remove(to, tier);
            lock (_lock) _persisted.Remove(to, tier);
        }
        removed.Dispose();
        return true;
    }

    public void RemovePersistedStatesBefore(ulong blockNumber)
    {
        List<(StateId To, SnapshotTier Tier)> states = [];
        lock (_lock)
            foreach (PbtPersistedSnapshot snapshot in _persisted.Snapshots)
                if (Height(snapshot.To) < (long)blockNumber) states.Add((snapshot.To, snapshot.Tier));
        foreach ((StateId to, SnapshotTier tier) in states) RemovePersistedExact(to, tier);
    }

    public bool RemoveMemorySource(PbtSnapshot snapshot)
    {
        bool removed = false;
        lock (_lock)
        {
            if (_snapshots.TryGetValue(snapshot.To, out PbtSnapshot? current) && ReferenceEquals(snapshot, current))
            {
                _snapshots.Remove(snapshot.To);
                Metrics.AddPbtBaseSnapshotCount(-1);
                if (_recordDetailedMetrics) Metrics.AddPbtBaseSnapshotMemory(snapshot.PayloadSize, -1);
                removed = true;
            }
            else if (_compactedSnapshots.TryGetValue(snapshot.To, out current) && ReferenceEquals(snapshot, current))
                removed = _compactedSnapshots.Remove(snapshot.To);
            if (removed) _memoryBytes -= Bytes(snapshot);
        }
        if (removed) snapshot.Dispose();
        return removed;
    }

    public ArrayPoolList<StateId> GetInMemoryStates()
    {
        lock (_lock)
        {
            ArrayPoolList<StateId> states = new(_snapshots.Count, _snapshots.Keys);
            states.AsSpan().Sort(CompareStates);
            return states;
        }
    }

    private static int CompareStates(StateId left, StateId right)
    {
        int height = Height(left).CompareTo(Height(right));
        return height != 0 ? height : left.StateRoot.Bytes.SequenceCompareTo(right.StateRoot.Bytes);
    }

    public StateId? GetLastSnapshotId()
    {
        lock (_lock)
        {
            StateId? winner = null;
            foreach (StateId state in _snapshots.Keys) Consider(state);
            foreach (StateId state in _compactedSnapshots.Keys) Consider(state);
            foreach (PbtPersistedSnapshot snapshot in _persisted.Snapshots) Consider(snapshot.To);
            return winner;
            void Consider(StateId state)
            {
                if (winner is null || CompareStates(state, winner.Value) > 0) winner = state;
            }
        }
    }

    public bool IsOnDisk(in StateId state, in StateId baseState)
    {
        lock (_lock) return state == baseState || HasPersistedBase(state);
    }

    public void MarkPersistedTierForShutdown()
    {
        lock (_lock) foreach (PbtPersistedSnapshot snapshot in _persisted.Snapshots) snapshot.PersistOnShutdown();
    }

    public void Dispose()
    {
        List<PbtSnapshot> memory;
        List<PbtPersistedSnapshot> persisted;
        lock (_publicationGate.Sync)
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                memory = [.. _snapshots.Values, .. _compactedSnapshots.Values];
                persisted = _persisted.Clear();
                Metrics.AddPbtBaseSnapshotCount(-_snapshots.Count);
                if (_recordDetailedMetrics) foreach (PbtSnapshot snapshot in _snapshots.Values) Metrics.AddPbtBaseSnapshotMemory(snapshot.PayloadSize, -1);
                _snapshots.Clear(); _compactedSnapshots.Clear(); _memoryBytes = 0;
            }
        foreach (PbtSnapshot snapshot in memory) snapshot.Dispose();
        foreach (PbtPersistedSnapshot snapshot in persisted) snapshot.Dispose();
    }

    public bool TryRemoveUnreachableFrom(in StateId head, in StateId persisted, out int removedCount)
    {
        removedCount = 0;
        lock (_publicationGate.Sync)
        {
            List<PbtSnapshot> memoryCandidates = [];
            List<(StateId To, SnapshotTier Tier)> persistedCandidates = [];
            HashSet<StateId> reachable;
            lock (_lock)
            {
                if (head != persisted && !_snapshots.ContainsKey(head) && !HasPersistedBase(head)) return false;
                foreach (PbtSnapshot snapshot in _snapshots.Values) memoryCandidates.Add(snapshot);
                foreach (PbtSnapshot snapshot in _compactedSnapshots.Values) memoryCandidates.Add(snapshot);
                foreach (PbtPersistedSnapshot snapshot in _persisted.Snapshots)
                    persistedCandidates.Add((snapshot.To, snapshot.Tier));
                reachable = CollectRewindAncestry(head);
                // The durable base is irreversible; pruning its connecting layers would strand this head.
                if (persisted != StateId.PreGenesis && !reachable.Contains(persisted)) return false;
            }

            HashSet<StateId> removedStates = [];
            foreach (PbtSnapshot snapshot in memoryCandidates)
                if (!reachable.Contains(snapshot.To) && RemoveMemorySource(snapshot)) removedStates.Add(snapshot.To);
            foreach ((StateId to, SnapshotTier tier) in persistedCandidates)
                if (!reachable.Contains(to) && RemovePersistedExact(to, tier)) removedStates.Add(to);
            lock (_lock) _lastCommittedStateId = head;
            removedCount = removedStates.Count;
            return true;
        }
    }

    // Caller holds the repository lock so candidate capture and ancestry see the same graph.
    private HashSet<StateId> CollectRewindAncestry(in StateId head)
    {
        HashSet<StateId> reachable = [head];
        Stack<StateId> pending = new();
        pending.Push(head);
        while (pending.TryPop(out StateId state))
        {
            if (_snapshots.TryGetValue(state, out PbtSnapshot? memory) && reachable.Add(memory.From)) pending.Push(memory.From);
            if (_compactedSnapshots.TryGetValue(state, out memory) && reachable.Add(memory.From)) pending.Push(memory.From);
            foreach (SnapshotTier tier in PersistedPriority)
                if (_persisted.TryGet(state, tier, out PbtPersistedSnapshot? snapshot) && reachable.Add(snapshot.From)) pending.Push(snapshot.From);
        }
        return reachable;
    }

    public bool ContainsPersistedStorageSource(PbtPersistedSnapshot snapshot)
    {
        lock (_lock)
            return _persisted.TryGet(snapshot.To, snapshot.Tier, out PbtPersistedSnapshot? current) && SameIdentity(current, snapshot);
    }

    private static bool SameIdentity(PbtPersistedSnapshot left, PbtPersistedSnapshot right) =>
        left.From == right.From && left.To == right.To && left.TreeRoot == right.TreeRoot
        && left.Tier == right.Tier && left.Location == right.Location;

    public bool ReplacePersistedSnapshot(PbtPersistedSnapshot expected, PbtPersistedSnapshot replacement)
    {
        lock (_publicationGate.Sync)
        {
            lock (_lock)
            {
                if (!_persisted.Contains(expected)) return false;
                if (!SameIdentity(replacement, expected))
                    throw new InvalidOperationException("Bloom replacement must preserve the persisted snapshot's identity and storage.");
                if (!_persisted.TryReplace(expected, replacement)) return false;
            }
            expected.Dispose();
            return true;
        }
    }

    public void ShareBloomAcrossRange(StateId from, StateId to, RefCountedBloomFilter sharedBloom)
    {
        lock (_publicationGate.Sync)
        {
            List<PbtPersistedSnapshot> candidates = [];
            try
            {
                lock (_lock)
                {
                    HashSet<StateId> ancestry = [to];
                    List<StateId> states = [];
                    StateId current = to;
                    while (Height(current) > Height(from))
                    {
                        if (!_persisted.TryGet(current, SnapshotTier.PersistedBase, out PbtPersistedSnapshot? baseSnapshot)) break;
                        states.Add(current);
                        if (!ancestry.Add(baseSnapshot.From)) break;
                        current = baseSnapshot.From;
                    }
                    foreach (StateId state in states)
                        foreach (SnapshotTier tier in PersistedPriority)
                            if (_persisted.TryGet(state, tier, out PbtPersistedSnapshot? snapshot)
                                && !ReferenceEquals(snapshot.BloomRef, sharedBloom) && Height(snapshot.From) >= Height(from)
                                && ancestry.Contains(snapshot.From) && snapshot.TryLease()) candidates.Add(snapshot);
                }

                // A clear may remove a descriptor from the widest diff that an earlier persisted
                // state still needs. Populate the historical superset before rebinding any reader.
                foreach (PbtPersistedSnapshot snapshot in candidates)
                    foreach (ulong key in snapshot.EntityBloomHashes())
                        if (!sharedBloom.Filter.MightContain(key)) sharedBloom.Filter.Add(key);
                foreach (PbtPersistedSnapshot snapshot in candidates)
                {
                    using PbtPersistedSnapshot replacement = snapshot.WithBloom(sharedBloom);
                    ReplacePersistedSnapshot(snapshot, replacement);
                }
            }
            finally
            {
                foreach (PbtPersistedSnapshot snapshot in candidates) snapshot.Dispose();
            }
        }
    }
}
