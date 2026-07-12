// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Trie;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Flat;

/// <summary>
/// Snapshot are written keys between state From to state To. Backed by either the mutable
/// <see cref="SnapshotContent"/> or the sorted <see cref="SortedSnapshotContent"/>, selected by <see cref="IsSorted"/>.
/// </summary>
public class Snapshot : RefCountingDisposable
{
    private readonly StateId _from;
    private readonly StateId _to;
    private readonly IResourcePool _resourcePool;
    private readonly ResourcePool.Usage _usage;
    private readonly SnapshotContent? _mutable;
    private readonly SortedSnapshotContent? _sorted;
    private readonly bool _isSorted;
    // Counts sealed on first observation: counting the concurrently written maps acquires their stripe locks, so
    // serving live counts would stall writers on each estimate, and the repository's
    // add/remove memory ledger needs the same value on both sides even if stragglers still write.
    // Sealing lazily (not in the constructor) keeps ResourcePool.CreateSnapshot's
    // construct-then-populate contract working: population always precedes the first count observation.
    // Boxed so publication is a single reference CAS.
    private StrongBox<SnapshotContentCounts>? _sealedCounts;

    public Snapshot(in StateId from, in StateId to, SnapshotContent content, IResourcePool resourcePool, ResourcePool.Usage usage)
    {
        _from = from;
        _to = to;
        _mutable = content;
        _resourcePool = resourcePool;
        _usage = usage;
        _isSorted = false;
    }

    public Snapshot(in StateId from, in StateId to, SortedSnapshotContent content, IResourcePool resourcePool, ResourcePool.Usage usage)
    {
        _from = from;
        _to = to;
        _sorted = content;
        _resourcePool = resourcePool;
        _usage = usage;
        _isSorted = true;
    }

    public long EstimateMemory() => _isSorted ? _sorted!.EstimateMemory() : Counts.EstimateMemory();
    public long EstimateCompactedMemory() => _isSorted ? _sorted!.EstimateCompactedMemory() : Counts.EstimateCompactedMemory();

    /// <summary>
    /// The mutable content's counts, captured once on first access and immutable afterwards. Racing
    /// first observers may compute different candidates, but CompareExchange publishes exactly one,
    /// so every reader (including the repository's add and remove ledger sides) sees the same value.
    /// </summary>
    private SnapshotContentCounts Counts
    {
        get
        {
            StrongBox<SnapshotContentCounts>? sealedCounts = Volatile.Read(ref _sealedCounts);
            if (sealedCounts is null)
            {
                StrongBox<SnapshotContentCounts> candidate = new(_mutable!.CaptureCounts());
                sealedCounts = Interlocked.CompareExchange(ref _sealedCounts, candidate, null) ?? candidate;
            }

            return sealedCounts.Value;
        }
    }
    // Test-only observability (SnapshotCompactorTests); not consumed by production.
    internal ResourcePool.Usage Usage => _usage;

    public bool IsSorted => _isSorted;

    public StateId From => _from;
    public StateId To => _to;
    public IEnumerable<KeyValuePair<HashedKey<Address>, Account?>> Accounts => _isSorted ? _sorted!.Accounts : _mutable!.Accounts;
    public IEnumerable<KeyValuePair<HashedKey<Address>, bool>> SelfDestructedStorageAddresses => _isSorted ? _sorted!.SelfDestructedStorageAddresses : _mutable!.SelfDestructedStorageAddresses;
    public IEnumerable<KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?>> Storages => _isSorted ? _sorted!.Storages : _mutable!.Storages;
    public IEnumerable<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>> StorageNodes => _isSorted ? _sorted!.StorageNodes : _mutable!.StorageNodes;
    public IEnumerable<KeyValuePair<HashedKey<TreePath>, TrieNode>> StateNodes => _isSorted ? _sorted!.StateNodes : _mutable!.StateNodes;
    public int AccountsCount => _isSorted ? _sorted!.AccountsCount : Counts.Accounts;
    public int StoragesCount => _isSorted ? _sorted!.StoragesCount : Counts.Storages;
    public int StateNodesCount => _isSorted ? _sorted!.StateNodesCount : Counts.StateNodes;
    public int StorageNodesCount => _isSorted ? _sorted!.StorageNodesCount : Counts.StorageNodes;

    /// <summary>The mutable content; only valid for snapshots created for commit (not compacted ones).</summary>
    public SnapshotContent Content => _mutable!;

    internal SortedSnapshotContent SortedContent => _sorted!;

    public bool TryGetAccount(HashedKey<Address> key, out Account? acc)
        => _isSorted ? _sorted!.TryGetAccount(key, out acc) : _mutable!.Accounts.TryGetValue(key, out acc);

    public bool HasSelfDestruct(HashedKey<Address> key)
        => _isSorted ? _sorted!.HasSelfDestruct(key) : _mutable!.SelfDestructedStorageAddresses.TryGetValue(key, out bool _);

    public bool TryGetStorage(HashedKey<(Address, UInt256)> key, out SlotValue? value)
        => _isSorted ? _sorted!.TryGetStorage(key, out value) : _mutable!.Storages.TryGetValue(key, out value);

    public bool TryGetStateNode(HashedKey<TreePath> key, [NotNullWhen(true)] out TrieNode? node)
        => _isSorted ? _sorted!.TryGetStateNode(key, out node) : _mutable!.StateNodes.TryGetValue(key, out node!);

    public bool TryGetStorageNode(HashedKey<(Hash256, TreePath)> key, [NotNullWhen(true)] out TrieNode? node)
        => _isSorted ? _sorted!.TryGetStorageNode(key, out node) : _mutable!.StorageNodes.TryGetValue(key, out node!);

    protected override void CleanUp()
    {
        if (_isSorted) _resourcePool.ReturnSortedSnapshotContent(_usage, _sorted!);
        else _resourcePool.ReturnSnapshotContent(_usage, _mutable!);
    }

    public bool TryAcquire() => TryAcquireLease();
}

public sealed class SnapshotContent : IDisposable, IResettable
{
    // ConcurrentDictionary: lock-free reads, best read latency for accounts/slots
    public readonly ConcurrentDictionary<HashedKey<Address>, Account?> Accounts = new();
    public readonly ConcurrentDictionary<HashedKey<(Address, UInt256)>, SlotValue?> Storages = new();
    public readonly ConcurrentDictionary<HashedKey<Address>, bool> SelfDestructedStorageAddresses = new();

    public readonly Dictionary<HashedKey<TreePath>, TrieNode> StateNodes = [];
    public readonly AddressStorageNodeDictionary StorageNodes = new();

    public void Reset()
    {
        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kvp in StateNodes) kvp.Value.PrunePersistedRecursively(1);
        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kvp in StorageNodes) kvp.Value.PrunePersistedRecursively(1);

        Accounts.NoResizeClear();
        Storages.NoResizeClear();
        SelfDestructedStorageAddresses.NoResizeClear();
        StateNodes.Clear();
        StorageNodes.Clear();
    }

    /// <summary>
    /// Captures all map counts in one pass. Counting the concurrently written maps acquires every stripe lock, so
    /// callers should capture once at a writer-quiescent boundary and reuse the result instead of re-reading live
    /// counts.
    /// </summary>
    public SnapshotContentCounts CaptureCounts() => new(
        Accounts.Count,
        Storages.Count,
        SelfDestructedStorageAddresses.Count,
        StateNodes.Count,
        StorageNodes.Count);

    public long EstimateMemory() => CaptureCounts().EstimateMemory();

    /// <inheritdoc cref="SnapshotContentCounts.EstimateCompactedMemory"/>
    public long EstimateCompactedMemory() => CaptureCounts().EstimateCompactedMemory();

    public void Dispose()
    {
    }
}

/// <summary>
/// Entry counts of a <see cref="SnapshotContent"/>, captured once so memory estimates are pure
/// functions of a single consistent capture.
/// </summary>
public readonly record struct SnapshotContentCounts(
    int Accounts,
    int Storages,
    int SelfDestructedStorageAddresses,
    int StateNodes,
    int StorageNodes)
{
    private const int NodeSizeEstimate = 650; // Counting the node size one by one has a notable overhead. So we use estimate.

    public long EstimateMemory() =>
        // Cast Count to long before multiplying to avoid int overflow for large snapshots
        (long)Accounts * 172 +                         // Key (12B: ref 8B + hash 4B) + Value ref (8B) + CD overhead (48) + Account object (~104B)
            (long)Storages * 136 +                         // Key (44B: addr ref 8B + UInt256 32B + hash 4B) + Value (40B SlotValue?) + CD overhead (48) + Value ref (4B)
            (long)SelfDestructedStorageAddresses * 64 +    // Key (12B: ref 8B + hash 4B) + Value (4B) + CD overhead (48)
            (long)StateNodes * (NodeSizeEstimate + 76) +   // Key (40B: TreePath 36B + hash 4B) + Value ref (8B) + dictionary overhead (28) + TrieNode
            (long)StorageNodes * (NodeSizeEstimate + 84);  // Key (48B: Hash256 ref 8B + TreePath 36B + hash 4B) + Value ref (8B) + dictionary overhead (28) + TrieNode

    /// <summary>
    /// Estimates memory for compacted snapshots, counting only dictionary overhead + keys + value-type values.
    /// Does not count reference type values (Account and TrieNode) as they are already accounted for
    /// by non-compacted snapshots (compacted snapshots share these references with the original snapshots).
    /// </summary>
    public long EstimateCompactedMemory() =>
        // ConcurrentDictionary entry overhead ~48 bytes
        // Reference type values (Account, TrieNode) not counted - already accounted by non-compacted snapshot
        (long)Accounts * 68 +                          // Key (12B: ref 8B + hash 4B) + Value ref (8B) + CD overhead (48)
            (long)Storages * 136 +                         // Key (44B: addr ref 8B + UInt256 32B + hash 4B) + Value (40B SlotValue?) + CD overhead (48) + Value ref (4B)
            (long)SelfDestructedStorageAddresses * 64 +    // Key (12B: ref 8B + hash 4B) + Value (4B) + CD overhead (48)
            (long)StateNodes * 76 +                        // Key (40B: TreePath 36B + hash 4B) + Value ref (8B) + dictionary overhead (28)
            (long)StorageNodes * 84;                       // Key (48B: Hash256 ref 8B + TreePath 36B + hash 4B) + Value ref (8B) + dictionary overhead (28)
}
