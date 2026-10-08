// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat;
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
public partial class PbtSnapshotRepository(IMetricsConfig metricsConfig) : IDisposable
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
}
