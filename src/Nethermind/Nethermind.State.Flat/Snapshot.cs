// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

    public long EstimateMemory() => _isSorted ? _sorted!.EstimateMemory() : _mutable!.EstimateMemory();
    public long EstimateCompactedMemory() => _isSorted ? _sorted!.EstimateCompactedMemory() : _mutable!.EstimateCompactedMemory();
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
    public int AccountsCount => _isSorted ? _sorted!.AccountsCount : _mutable!.Accounts.Count;
    public int StoragesCount => _isSorted ? _sorted!.StoragesCount : _mutable!.Storages.Count;
    public int StateNodesCount => _isSorted ? _sorted!.StateNodesCount : _mutable!.StateNodes.Count;
    public int StorageNodesCount => _isSorted ? _sorted!.StorageNodesCount : _mutable!.StorageNodes.Count;

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
    private const int NodeSizeEstimate = 650; // Counting the node size one by one has a notable overhead. So we use estimate.

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

        // Reset runs at a quiescent pool-return boundary (final snapshot lease released, or the
        // bundle returning its current content on disposal), so no writer can hold a stripe;
        // the lock-free clear skips ~thousands of inflated Monitor acquisitions per return.
        Accounts.NoLockClear();
        Storages.NoLockClear();
        SelfDestructedStorageAddresses.NoLockClear();
        StateNodes.Clear();
        StorageNodes.NoLockClear();
    }

    public long EstimateMemory() =>
        // Cast Count to long before multiplying to avoid int overflow for large snapshots
        (long)Accounts.Count * 172 +                         // Key (12B: ref 8B + hash 4B) + Value ref (8B) + CD overhead (48) + Account object (~104B)
            (long)Storages.Count * 136 +                         // Key (44B: addr ref 8B + UInt256 32B + hash 4B) + Value (40B SlotValue?) + CD overhead (48) + Value ref (4B)
            (long)SelfDestructedStorageAddresses.Count * 64 +    // Key (12B: ref 8B + hash 4B) + Value (4B) + CD overhead (48)
            (long)StateNodes.Count * (NodeSizeEstimate + 76) +   // Key (40B: TreePath 36B + hash 4B) + Value ref (8B) + dictionary overhead (28) + TrieNode
            (long)StorageNodes.Count * (NodeSizeEstimate + 84);  // Key (48B: Hash256 ref 8B + TreePath 36B + hash 4B) + Value ref (8B) + dictionary overhead (28) + TrieNode

    /// <summary>
    /// Estimates memory for compacted snapshots, counting only dictionary overhead + keys + value-type values.
    /// Does not count reference type values (Account and TrieNode) as they are already accounted for
    /// by non-compacted snapshots (compacted snapshots share these references with the original snapshots).
    /// </summary>
    public long EstimateCompactedMemory() =>
        // ConcurrentDictionary entry overhead ~48 bytes
        // Reference type values (Account, TrieNode) not counted - already accounted by non-compacted snapshot
        Accounts.Count * 68 +                          // Key (12B: ref 8B + hash 4B) + Value ref (8B) + CD overhead (48)
            Storages.Count * 136 +                         // Key (44B: addr ref 8B + UInt256 32B + hash 4B) + Value (40B SlotValue?) + CD overhead (48) + Value ref (4B)
            SelfDestructedStorageAddresses.Count * 64 +    // Key (12B: ref 8B + hash 4B) + Value (4B) + CD overhead (48)
            StateNodes.Count * 76 +                        // Key (40B: TreePath 36B + hash 4B) + Value ref (8B) + dictionary overhead (28)
            StorageNodes.Count * 84;                       // Key (48B: Hash256 ref 8B + TreePath 36B + hash 4B) + Value ref (8B) + dictionary overhead (28)

    public void Dispose()
    {
    }
}
