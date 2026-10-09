// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt;

/// <summary>
/// Holds the in-memory diff layers keyed by their <see cref="PbtSnapshot.To"/> state, including
/// fork siblings, and assembles backward chains for bundle construction.
/// </summary>
/// <remarks>
/// Memory base and compacted layers coexist with four retained tiers. Compacted edges are traversal
/// shortcuts, not substitutes for independently available base snapshots.
/// </remarks>
public class PbtSnapshotRepository(IMetricsConfig metricsConfig) : IDisposable
{
    // Metrics are optional; operational memory accounting must remain active for byte-budget enforcement.
    private readonly bool _recordDetailedMetrics = metricsConfig.EnableDetailedMetric;
    private readonly Lock _lock = new();
    private readonly Dictionary<StateId, PbtSnapshot> _snapshots = [];
    private readonly Dictionary<StateId, PbtSnapshot> _compactedSnapshots = [];
    private StateId? _lastCommittedStateId;
    private readonly Dictionary<(StateId To, long Depth), PbtRetainedSnapshot> _retained = [];
    private readonly ISnapshotCatalog _catalog = NullSnapshotCatalog.Instance;
    private readonly PbtRetainedPublicationGate _publicationGate = new();
    private long _memoryBytes;
    private bool _disposed;

    internal PbtSnapshotRepository(IMetricsConfig metricsConfig, ISnapshotCatalog catalog, PbtRetainedPublicationGate publicationGate)
        : this(metricsConfig) => (_catalog, _publicationGate) = (catalog, publicationGate);

    private readonly PbtRetainedStorageLifetime _storageLifetime = new();
    internal PbtSnapshotRepository(IMetricsConfig metricsConfig, ISnapshotCatalog catalog, PbtRetainedPublicationGate publicationGate, PbtRetainedStorageLifetime storageLifetime)
        : this(metricsConfig, catalog, publicationGate) => _storageLifetime = storageLifetime;

    internal PbtRetainedPublicationGate PublicationGate => _publicationGate;
    internal long InMemorySnapshotBytes { get { lock (_lock) return _memoryBytes; } }
    internal int RetainedCount { get { lock (_lock) return _retained.Count; } }
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
                _memoryBytes += payloadSize.Leaf + payloadSize.Node;
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
        PbtSnapshotPayloadSize payloadSize = snapshot.PayloadSize;
        lock (_lock)
        {
            if (!_disposed && _compactedSnapshots.TryAdd(snapshot.To, snapshot)) { _memoryBytes += payloadSize.Leaf + payloadSize.Node; return true; }
        }

        snapshot.Dispose();
        return false;
    }

    /// <summary>Leases the next bounded snapshot extending the persisted state, preferring compacted edges in a backward breadth-first walk.</summary>
    internal PbtSnapshot? FindSnapshotToPersist(in StateId seed, in StateId persistedState, ulong compactSize)
    {
        using PbtSnapshotLease? lease = FindCandidateToPersist(seed, persistedState, compactSize, static _ => true, memoryOnly: true);
        if (lease?.Memory is not { } snapshot || !snapshot.TryLease()) return null;
        return snapshot;
    }

    /// <summary>Releases orphan descendants above the successful persistence boundary while preserving leased readers.</summary>
    /// <remarks>Call before removing states at the boundary, whose siblings identify the forks to prune.</remarks>
    internal void RemoveSiblingAndDescendents(in StateId canonicalState)
    {
        List<StateId> abandoned = [];
        List<PbtSnapshot> memoryCandidates = [];
        List<(StateId To, long Depth, SnapshotTier Tier)> retained = [];
        lock (_publicationGate.Sync)
        {
            lock (_lock)
            {
                HashSet<StateId> states = [.. _snapshots.Keys, .. _compactedSnapshots.Keys];
                foreach (PbtRetainedSnapshot snapshot in _retained.Values) states.Add(snapshot.To);
                bool hasSibling = false;
                foreach (StateId state in states) hasSibling |= state.BlockNumber == canonicalState.BlockNumber && state != canonicalState;
                if (!hasSibling) return;
                List<StateId> ordered = [.. states];
                ordered.Sort(CompareStates);
                HashSet<StateId> reachable = [canonicalState];
                // A retained descendant cannot rely on memory below it; backward reads cannot return to memory.
                HashSet<StateId> retainedReachable = [canonicalState];
                foreach (StateId state in ordered)
                {
                    if (Height(state) <= Height(canonicalState)) continue;
                    bool connected = _snapshots.TryGetValue(state, out PbtSnapshot? memory) && reachable.Contains(memory.From)
                        || _compactedSnapshots.TryGetValue(state, out memory) && reachable.Contains(memory.From);
                    bool retainedConnected = false;
                    foreach (SnapshotTier tier in RetainedPriority)
                    {
                        if (!_retainedEdges.TryGetValue((state, tier), out SortedDictionary<long, PbtRetainedSnapshot>? edges)) continue;
                        foreach (PbtRetainedSnapshot snapshot in edges.Values) retainedConnected |= retainedReachable.Contains(snapshot.From);
                    }
                    if (retainedConnected) retainedReachable.Add(state);
                    if (connected || retainedConnected) reachable.Add(state);
                    else abandoned.Add(state);
                }
                foreach (StateId state in abandoned)
                {
                    if (_snapshots.TryGetValue(state, out PbtSnapshot? snapshot)) memoryCandidates.Add(snapshot);
                    if (_compactedSnapshots.TryGetValue(state, out snapshot)) memoryCandidates.Add(snapshot);
                }
                HashSet<StateId> abandonedSet = [.. abandoned];
                foreach (((StateId to, long depth), PbtRetainedSnapshot snapshot) in _retained)
                    if (abandonedSet.Contains(to)) retained.Add((to, depth, snapshot.Tier));
            }
            foreach (PbtSnapshot snapshot in memoryCandidates) RemoveMemorySource(snapshot);
            foreach ((StateId to, long depth, SnapshotTier tier) in retained) RemoveRetainedExact(to, depth, tier);
        }
    }

    public bool HasState(in StateId stateId)
    {
        lock (_lock) return _snapshots.ContainsKey(stateId) || HasRetainedBase(stateId);
    }

    /// <summary>
    /// Leases the chain of layers from <paramref name="head"/> (inclusive) down to
    /// <paramref name="persistedFloor"/> (exclusive), oldest first. Returns false when the chain is
    /// broken, e.g. pruned concurrently; the caller should re-read the persisted floor and retry.
    /// </summary>
    /// <remarks>
    /// <paramref name="chain"/> is owned by the caller either way: on failure it is left holding the
    /// leases taken before the walk broke, which disposing it releases.
    /// </remarks>
    public bool TryLeaseChain(in StateId head, in StateId persistedFloor, PbtSnapshotPooledList chain)
    {
        StateId floor = persistedFloor;
        using PbtSnapshotChain? leased = head == floor ? new([]) : Walk(head, MemoryPriority,
            edge => edge.From == floor ? Step.Stop : Height(edge.From) > Height(floor) ? Step.Traverse : Step.Skip);
        if (leased is null) return false;
        foreach (PbtSnapshotLease edge in leased.Layers)
        {
            if (!edge.Memory!.TryLease()) throw new InvalidOperationException("Held snapshot lease expired.");
            chain.Add(edge.Memory);
        }
        return true;
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
        if (Height(head) == minBlockNumber) return true;
        using PbtSnapshotChain? leased = Walk(head, MemoryPriority, edge =>
            Height(edge.From) < minBlockNumber ? Step.Skip : Height(edge.From) == minBlockNumber ? Step.Stop : Step.Traverse);
        if (leased is null) return false;
        foreach (PbtSnapshotLease edge in leased.Layers)
        {
            if (!edge.Memory!.TryLease()) throw new InvalidOperationException("Held snapshot lease expired.");
            chain.Add(edge.Memory);
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

    internal bool CanReachState(in StateId head, in StateId target)
    {
        if (head == target) return true;
        StateId wanted = target;
        using PbtSnapshotChain? chain = Walk(head, ReadPriority, edge =>
            edge.From == wanted ? Step.Stop : Height(edge.From) > Height(wanted) ? Step.Traverse : Step.Skip);
        return chain is not null;
    }

    private static readonly ValueHash256 MaximumRetainedRoot = CreateMaximumRoot();

    private static ValueHash256 CreateMaximumRoot()
    {
        Span<byte> bytes = stackalloc byte[32];
        bytes.Fill(byte.MaxValue);
        return new(bytes);
    }

    internal ArrayPoolList<StateId> GetRetainedStatesInRange(ulong first, ulong last)
    {
        ArrayPoolList<StateId> states = new(16);
        lock (_lock)
            foreach (StateId state in _retainedStateIds.GetViewBetween(new(first, default), new(last, MaximumRetainedRoot)))
                states.Add(state);
        return states;
    }

    private void RemoveRetainedState(in StateId state)
    {
        List<(long Depth, SnapshotTier Tier)> entries = [];
        lock (_lock)
            foreach (SnapshotTier tier in RetainedPriority)
                if (_retainedEdges.TryGetValue((state, tier), out SortedDictionary<long, PersistedSnapshots.PbtRetainedSnapshot>? edges))
                    foreach (long depth in edges.Keys) entries.Add((depth, tier));
        foreach ((long depth, SnapshotTier tier) in entries) RemoveRetainedExact(state, depth, tier);
    }

    internal void RemoveFinalizedRetainedForks(in StateId persisted, IStateHeaderProvider provider)
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
                using ArrayPoolList<StateId> states = GetRetainedStatesInRange(first, last);
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
                    RemoveRetainedState(state);
                }
                if (last == finalized) break;
                first = last + 1;
            }
        }
    }

    private static readonly SnapshotTier[] ReadPriority = [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase];
    private static readonly SnapshotTier[] PersistPriority = [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.InMemoryCompacted, SnapshotTier.PersistedSmallCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedBase];
    private static readonly SnapshotTier[] RetainedPriority = [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase];
    private static readonly SnapshotTier[] MemoryPriority = [SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase];
    private readonly Dictionary<(StateId To, SnapshotTier Tier), SortedDictionary<long, PbtRetainedSnapshot>> _retainedEdges = [];
    private readonly SortedSet<StateId> _retainedStateIds = new(Comparer<StateId>.Create(CompareStates));
    private readonly Dictionary<StateId, int> _retainedStateCounts = [];
    private enum Step { Skip, Traverse, Win, Stop }

    internal bool TryAddRetained(PbtRetainedSnapshot snapshot)
    {
        lock (_publicationGate.Sync)
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                (StateId, long) key = (snapshot.To, unchecked((long)(snapshot.To.BlockNumber - snapshot.From.BlockNumber)));
                if (_retained.ContainsKey(key)) return false;
                if (!snapshot.TryLease()) return false;
                _retained.Add(key, snapshot);
                if (!_retainedEdges.TryGetValue((snapshot.To, snapshot.Tier), out SortedDictionary<long, PbtRetainedSnapshot>? edges))
                    _retainedEdges.Add((snapshot.To, snapshot.Tier), edges = []);
                edges.Add(key.Item2, snapshot);
                _retainedStateCounts.TryGetValue(snapshot.To, out int count);
                _retainedStateCounts[snapshot.To] = count + 1;
                _retainedStateIds.Add(snapshot.To);
                return true;
            }
    }

    internal bool ContainsMemorySource(PbtSnapshot snapshot)
    {
        lock (_lock) return _snapshots.TryGetValue(snapshot.To, out PbtSnapshot? value) && ReferenceEquals(snapshot, value)
            || _compactedSnapshots.TryGetValue(snapshot.To, out value) && ReferenceEquals(snapshot, value);
    }

    internal bool ContainsRetainedSource(PbtRetainedSnapshot snapshot)
    {
        lock (_lock) return _retained.TryGetValue((snapshot.To, unchecked((long)(snapshot.To.BlockNumber - snapshot.From.BlockNumber))), out PbtRetainedSnapshot? value) && ReferenceEquals(value, snapshot);
    }

    internal bool TryLeaseRetained(in StateId to, long depth, SnapshotTier tier, out PbtRetainedSnapshot? snapshot)
    {
        lock (_lock)
        {
            if (_retained.TryGetValue((to, depth), out snapshot) && snapshot.Tier == tier && snapshot.TryLease()) return true;
            snapshot = null;
            return false;
        }
    }

    internal bool TryLeaseMemoryState(in StateId state, SnapshotTier tier, out PbtSnapshot? snapshot)
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

    internal bool TryLeaseRetainedCatalogKey(in StateId to, long depth, out PbtRetainedSnapshot? snapshot)
    {
        lock (_lock)
        {
            if (_retained.TryGetValue((to, depth), out snapshot) && snapshot.TryLease()) return true;
            snapshot = null;
            return false;
        }
    }

    internal (StateId To, long Depth, SnapshotTier Tier)[] GetRetainedEntries()
    {
        lock (_lock)
        {
            List<(StateId, long, SnapshotTier)> result = [];
            foreach (((StateId to, long depth), PbtRetainedSnapshot snapshot) in _retained) result.Add((to, depth, snapshot.Tier));
            return result.ToArray();
        }
    }

    private bool HasRetainedBase(in StateId state) => _retainedEdges.ContainsKey((state, SnapshotTier.PersistedBase));

    internal PbtSnapshotChain? TryLeaseReadChain(in StateId target, in StateId readerFloor)
    {
        if (target == readerFloor) return new([]);
        StateId floor = readerFloor;
        return Walk(target, ReadPriority, (edge) => Height(edge.From) < Height(floor)
            ? edge.Tier.IsPersisted() ? Step.Stop : Step.Skip
            : edge.From == floor ? Step.Stop : edge.From.BlockNumber == floor.BlockNumber ? Step.Skip : Step.Traverse);
    }

    internal PbtSnapshotChain? TryLeaseRetainedChain(in StateId target, ulong minimumBlockNumber)
    {
        long best = long.MaxValue;
        return Walk(target, RetainedPriority, edge =>
        {
            if (Height(edge.From) < (long)minimumBlockNumber) return Step.Skip;
            if (edge.From.BlockNumber == minimumBlockNumber) return Step.Stop;
            if (Height(edge.From) < best) { best = Height(edge.From); return Step.Win; }
            return Step.Traverse;
        });
    }

    internal PbtSnapshotLease? FindCandidateToPersist(in StateId seed, in StateId baseState, ulong compactSize,
        Func<StateId, bool> acceptsFinalizedRoot, bool memoryOnly = false)
    {
        StateId floor = baseState;
        using PbtSnapshotChain? chain = Walk(seed, memoryOnly ? MemoryPriority : PersistPriority, edge =>
            edge.From == floor ? unchecked(edge.To.BlockNumber - edge.From.BlockNumber) <= compactSize && acceptsFinalizedRoot(edge.To) ? Step.Stop : Step.Skip
            : Height(edge.From) > Height(floor) ? Step.Traverse : Step.Skip);
        return chain is { Layers.Count: > 0 } ? chain.Layers[0].Lease() : null;
    }

    private PbtSnapshotChain? Walk(in StateId start, SnapshotTier[] priority, Func<PbtSnapshotLease, Step> decide)
    {
        List<(PbtSnapshotLease Edge, int Parent)> visited = [];
        Queue<(StateId State, bool Retained, int Parent)> queue = new();
        HashSet<StateId> seen = [start];
        queue.Enqueue((start, false, -1));
        int winner = -1;
        try
        {
            while (queue.TryDequeue(out (StateId State, bool Retained, int Parent) node))
            {
                foreach (SnapshotTier tier in priority)
                {
                    if (node.Retained && !tier.IsPersisted()) continue;
                    PbtSnapshotLease? edge = LeaseEdge(node.State, tier);
                    if (edge is null) continue;
                    Step step;
                    try { step = decide(edge); }
                    catch { edge.Dispose(); throw; }
                    if (step == Step.Skip || !seen.Add(edge.From)) { edge.Dispose(); continue; }
                    int index = visited.Count;
                    visited.Add((edge, node.Parent));
                    if (step is Step.Win or Step.Stop) winner = index;
                    if (step == Step.Stop) return Gather(visited, winner);
                    queue.Enqueue((edge.From, tier.IsPersisted(), index));
                }
            }
            return winner < 0 ? null : Gather(visited, winner);
        }
        finally { foreach ((PbtSnapshotLease edge, _) in visited) edge.Dispose(); }
    }

    private PbtSnapshotLease? LeaseEdge(in StateId state, SnapshotTier tier)
    {
        lock (_lock)
        {
            if (!tier.IsPersisted())
            {
                Dictionary<StateId, PbtSnapshot> source = tier == SnapshotTier.InMemoryBase ? _snapshots : _compactedSnapshots;
                return source.TryGetValue(state, out PbtSnapshot? snapshot) && snapshot.TryLease() ? new(snapshot, tier) : null;
            }
            if (!_retainedEdges.TryGetValue((state, tier), out SortedDictionary<long, PbtRetainedSnapshot>? edges)) return null;
            PbtRetainedSnapshot? widest = null;
            foreach (PbtRetainedSnapshot snapshot in edges.Values) widest = snapshot;
            return widest is not null && widest.TryLease() ? new(widest) : null;
        }
    }

    private static PbtSnapshotChain Gather(List<(PbtSnapshotLease Edge, int Parent)> visited, int winner)
    {
        List<PbtSnapshotLease> layers = [];
        try
        {
            for (int i = winner; i >= 0; i = visited[i].Parent) layers.Add(visited[i].Edge.Lease());
            return new(layers);
        }
        catch { foreach (PbtSnapshotLease layer in layers) layer.Dispose(); throw; }
    }

    internal bool RemoveRetainedExact(in StateId to, long depth, SnapshotTier tier)
    {
        PbtRetainedSnapshot? removed;
        lock (_publicationGate.Sync)
        {
            lock (_lock)
                if (!_retained.TryGetValue((to, depth), out removed) || removed.Tier != tier) return false;
            _catalog.Remove(to, depth);
            lock (_lock)
            {
                _retained.Remove((to, depth));
                SortedDictionary<long, PbtRetainedSnapshot> edges = _retainedEdges[(to, tier)];
                edges.Remove(depth);
                if (edges.Count == 0) _retainedEdges.Remove((to, tier));
                int count = _retainedStateCounts[to] - 1;
                if (count == 0)
                {
                    _retainedStateCounts.Remove(to);
                    _retainedStateIds.Remove(to);
                }
                else _retainedStateCounts[to] = count;
            }
        }
        removed.Dispose();
        return true;
    }

    internal void RemoveRetainedStatesBefore(ulong blockNumber)
    {
        List<(StateId To, long Depth, SnapshotTier Tier)> states = [];
        lock (_lock)
            foreach (((StateId to, long depth), PbtRetainedSnapshot snapshot) in _retained)
                if (Height(to) < (long)blockNumber) states.Add((to, depth, snapshot.Tier));
        foreach ((StateId to, long depth, SnapshotTier tier) in states) RemoveRetainedExact(to, depth, tier);
    }

    internal bool RemoveMemorySource(PbtSnapshot snapshot)
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

    internal bool RemoveRetainedSource(PbtRetainedSnapshot snapshot)
    {
        lock (_publicationGate.Sync)
        {
            if (!ContainsRetainedSource(snapshot)) return false;
            return RemoveRetainedExact(snapshot.To, unchecked((long)(snapshot.To.BlockNumber - snapshot.From.BlockNumber)), snapshot.Tier);
        }
    }

    internal void RemoveMemoryState(in StateId state)
    {
        List<PbtSnapshot> removed = [];
        lock (_lock)
        {
            if (_snapshots.Remove(state, out PbtSnapshot? snapshot))
            {
                Metrics.AddPbtBaseSnapshotCount(-1);
                if (_recordDetailedMetrics) Metrics.AddPbtBaseSnapshotMemory(snapshot.PayloadSize, -1);
                removed.Add(snapshot);
            }
            if (_compactedSnapshots.Remove(state, out snapshot)) removed.Add(snapshot);
            foreach (PbtSnapshot layer in removed) _memoryBytes -= Bytes(layer);
        }
        foreach (PbtSnapshot snapshot in removed) snapshot.Dispose();
    }

    internal StateId[] GetInMemoryStates()
    {
        lock (_lock)
        {
            StateId[] states = [.. _snapshots.Keys];
            Array.Sort(states, CompareStates);
            return states;
        }
    }

    internal StateId[] GetRetainedStates()
    {
        lock (_lock)
        {
            HashSet<StateId> unique = [];
            foreach (PbtRetainedSnapshot snapshot in _retained.Values) unique.Add(snapshot.To);
            StateId[] states = [.. unique];
            Array.Sort(states, CompareStates);
            return states;
        }
    }

    private static int CompareStates(StateId left, StateId right)
    {
        int height = Height(left).CompareTo(Height(right));
        return height != 0 ? height : left.StateRoot.Bytes.SequenceCompareTo(right.StateRoot.Bytes);
    }

    internal StateId? GetLastSnapshotId()
    {
        lock (_lock)
        {
            StateId? winner = null;
            foreach (StateId state in _snapshots.Keys) Consider(state);
            foreach (StateId state in _compactedSnapshots.Keys) Consider(state);
            foreach (PbtRetainedSnapshot snapshot in _retained.Values) Consider(snapshot.To);
            return winner;
            void Consider(StateId state)
            {
                if (winner is null || CompareStates(state, winner.Value) > 0) winner = state;
            }
        }
    }

    internal bool IsOnDisk(in StateId state, in StateId baseState)
    {
        lock (_lock) return state == baseState || HasRetainedBase(state);
    }

    internal void SetLastCommittedStateId(in StateId state) { lock (_lock) _lastCommittedStateId = state; }

    internal void MarkPersistedTierForShutdown()
    {
        lock (_lock) foreach (PbtRetainedSnapshot snapshot in _retained.Values) snapshot.PersistOnShutdown();
    }

    public void Dispose()
    {
        List<PbtSnapshot> memory;
        List<PbtRetainedSnapshot> retained;
        lock (_publicationGate.Sync)
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                memory = [.. _snapshots.Values, .. _compactedSnapshots.Values];
                retained = [.. _retained.Values];
                Metrics.AddPbtBaseSnapshotCount(-_snapshots.Count);
                if (_recordDetailedMetrics) foreach (PbtSnapshot snapshot in _snapshots.Values) Metrics.AddPbtBaseSnapshotMemory(snapshot.PayloadSize, -1);
                _snapshots.Clear(); _compactedSnapshots.Clear(); _retained.Clear(); _retainedEdges.Clear(); _retainedStateIds.Clear(); _retainedStateCounts.Clear(); _memoryBytes = 0;
            }
        foreach (PbtSnapshot snapshot in memory) snapshot.Dispose();
        foreach (PbtRetainedSnapshot snapshot in retained) snapshot.Dispose();
    }

    internal bool TryRemoveUnreachableFrom(in StateId head, in StateId persisted, out int removedCount)
    {
        removedCount = 0;
        lock (_publicationGate.Sync)
        {
            List<PbtSnapshot> memoryCandidates = [];
            List<(StateId To, long Depth, SnapshotTier Tier)> retainedCandidates = [];
            HashSet<StateId> reachable;
            lock (_lock)
            {
                if (head != persisted && !_snapshots.ContainsKey(head) && !HasRetainedBase(head)) return false;
                foreach (PbtSnapshot snapshot in _snapshots.Values) memoryCandidates.Add(snapshot);
                foreach (PbtSnapshot snapshot in _compactedSnapshots.Values) memoryCandidates.Add(snapshot);
                foreach (((StateId to, long depth), PbtRetainedSnapshot snapshot) in _retained)
                    retainedCandidates.Add((to, depth, snapshot.Tier));
                reachable = CollectRewindAncestry(head);
                // The durable base is irreversible; pruning its connecting layers would strand this head.
                if (persisted != StateId.PreGenesis && !reachable.Contains(persisted)) return false;
            }

            HashSet<StateId> removedStates = [];
            foreach (PbtSnapshot snapshot in memoryCandidates)
                if (!reachable.Contains(snapshot.To) && RemoveMemorySource(snapshot)) removedStates.Add(snapshot.To);
            foreach ((StateId to, long depth, SnapshotTier tier) in retainedCandidates)
                if (!reachable.Contains(to) && RemoveRetainedExact(to, depth, tier)) removedStates.Add(to);
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
            foreach (SnapshotTier tier in RetainedPriority)
                if (_retainedEdges.TryGetValue((state, tier), out SortedDictionary<long, PbtRetainedSnapshot>? edges))
                    foreach (PbtRetainedSnapshot snapshot in edges.Values)
                        if (reachable.Add(snapshot.From)) pending.Push(snapshot.From);
        }
        return reachable;
    }

    internal bool ContainsRetainedStorageSource(PbtRetainedSnapshot snapshot)
    {
        lock (_lock)
            return _retained.TryGetValue((snapshot.To, unchecked((long)(snapshot.To.BlockNumber - snapshot.From.BlockNumber))), out PbtRetainedSnapshot? current)
                && current.From == snapshot.From && current.To == snapshot.To && current.TreeRoot == snapshot.TreeRoot
                && current.Tier == snapshot.Tier && current.Location == snapshot.Location;
    }

    internal bool ReplaceRetainedSnapshot(PbtRetainedSnapshot expected, PbtRetainedSnapshot replacement)
    {
        lock (_publicationGate.Sync)
        {
            lock (_lock)
            {
                (StateId, long) key = (expected.To, unchecked((long)(expected.To.BlockNumber - expected.From.BlockNumber)));
                if (!_retained.TryGetValue(key, out PbtRetainedSnapshot? current) || !ReferenceEquals(current, expected)) return false;
                if (replacement.From != expected.From || replacement.To != expected.To || replacement.TreeRoot != expected.TreeRoot
                    || replacement.Tier != expected.Tier || replacement.Location != expected.Location)
                    throw new InvalidOperationException("Bloom replacement must preserve the retained snapshot's identity and storage.");
                if (!replacement.TryLease()) return false;
                _retained[key] = replacement;
                _retainedEdges[(expected.To, expected.Tier)][key.Item2] = replacement;
            }
            expected.Dispose();
            return true;
        }
    }

    internal void ShareBloomAcrossRange(StateId from, StateId to, RefCountedBloomFilter sharedBloom)
    {
        lock (_publicationGate.Sync)
        {
            List<PbtRetainedSnapshot> candidates = [];
            try
            {
                lock (_lock)
                {
                    HashSet<StateId> ancestry = [to];
                    List<StateId> states = [];
                    StateId current = to;
                    while (Height(current) > Height(from))
                    {
                        if (!_retainedEdges.TryGetValue((current, SnapshotTier.PersistedBase), out SortedDictionary<long, PbtRetainedSnapshot>? bases)
                            || bases.Count == 0) break;
                        PbtRetainedSnapshot? baseSnapshot = null;
                        foreach (PbtRetainedSnapshot snapshot in bases.Values) { baseSnapshot = snapshot; break; }
                        states.Add(current);
                        if (!ancestry.Add(baseSnapshot!.From)) break;
                        current = baseSnapshot.From;
                    }
                    foreach (StateId state in states)
                        foreach (SnapshotTier tier in RetainedPriority)
                            if (_retainedEdges.TryGetValue((state, tier), out SortedDictionary<long, PbtRetainedSnapshot>? edges))
                                foreach (PbtRetainedSnapshot snapshot in edges.Values)
                                    if (!ReferenceEquals(snapshot.BloomRef, sharedBloom) && Height(snapshot.From) >= Height(from)
                                        && ancestry.Contains(snapshot.From) && snapshot.TryLease()) candidates.Add(snapshot);
                }

                // A clear may remove a descriptor from the widest diff that an earlier retained
                // state still needs. Populate the historical superset before rebinding any reader.
                foreach (PbtRetainedSnapshot snapshot in candidates)
                {
                    using PbtRetainedScanner scanner = snapshot.Scan();
                    while (scanner.MoveNext())
                        if (scanner.Key[0] is not 0 and not PbtRetainedKey.Ownership && !PbtRetainedSnapshot.IsChunk(scanner.Key))
                        {
                            ulong key = PbtRetainedKey.BloomHash(scanner.Key);
                            if (!sharedBloom.Filter.MightContain(key)) sharedBloom.Filter.Add(key);
                        }
                }
                foreach (PbtRetainedSnapshot snapshot in candidates)
                {
                    using PbtRetainedSnapshot replacement = snapshot.WithBloom(sharedBloom);
                    ReplaceRetainedSnapshot(snapshot, replacement);
                }
            }
            finally
            {
                foreach (PbtRetainedSnapshot snapshot in candidates) snapshot.Dispose();
            }
        }
    }
}
