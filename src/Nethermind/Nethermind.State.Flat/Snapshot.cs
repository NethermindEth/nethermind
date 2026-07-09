// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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
/// <see cref="SnapshotContent"/> or the sorted <see cref="MergedSnapshotContent"/>, selected by <see cref="IsSorted"/>.
/// </summary>
public class Snapshot : RefCountingDisposable
{
    private readonly StateId _from;
    private readonly StateId _to;
    private readonly IResourcePool _resourcePool;
    private readonly ResourcePool.Usage _usage;
    private readonly SnapshotContent? _mutable;
    private readonly MergedSnapshotContent? _merged;
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

    public Snapshot(in StateId from, in StateId to, MergedSnapshotContent content, IResourcePool resourcePool, ResourcePool.Usage usage)
    {
        _from = from;
        _to = to;
        _merged = content;
        _resourcePool = resourcePool;
        _usage = usage;
        _isSorted = true;
    }

    public long EstimateMemory() => _isSorted ? _merged!.EstimateMemory() : _mutable!.EstimateMemory();
    public long EstimateCompactedMemory() => _isSorted ? _merged!.EstimateCompactedMemory() : _mutable!.EstimateCompactedMemory();
    public ResourcePool.Usage Usage => _usage;

    public bool IsSorted => _isSorted;

    public StateId From => _from;
    public StateId To => _to;
    public IEnumerable<KeyValuePair<HashedKey<Address>, Account?>> Accounts => _isSorted ? _merged!.Accounts : _mutable!.Accounts;
    public IEnumerable<KeyValuePair<HashedKey<Address>, bool>> SelfDestructedStorageAddresses => _isSorted ? _merged!.SelfDestructedStorageAddresses : _mutable!.SelfDestructedStorageAddresses;
    public IEnumerable<KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?>> Storages => _isSorted ? _merged!.Storages : _mutable!.Storages;
    public IEnumerable<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>> StorageNodes => _isSorted ? _merged!.StorageNodes : _mutable!.StorageNodes;
    public IEnumerable<(Hash256, TreePath)> StorageTrieNodeKeys => StorageNodes.Select(static kvp => kvp.Key.Key);
    public IEnumerable<KeyValuePair<HashedKey<TreePath>, TrieNode>> StateNodes => _isSorted ? _merged!.StateNodes : _mutable!.StateNodes;
    public IEnumerable<TreePath> StateNodeKeys => StateNodes.Select(static kvp => kvp.Key.Key);
    public int AccountsCount => _isSorted ? _merged!.AccountsCount : _mutable!.Accounts.Count;
    public int StoragesCount => _isSorted ? _merged!.StoragesCount : _mutable!.Storages.Count;
    public int StateNodesCount => _isSorted ? _merged!.StateNodesCount : _mutable!.StateNodes.Count;
    public int StorageNodesCount => _isSorted ? _merged!.StorageNodesCount : _mutable!.StorageNodes.Count;

    /// <summary>The mutable content; only valid for snapshots created for commit (not compacted ones).</summary>
    public SnapshotContent Content => _mutable!;

    internal MergedSnapshotContent MergedContent => _merged!;

    public bool TryGetAccount(HashedKey<Address> key, out Account? acc)
        => _isSorted ? _merged!.TryGetAccount(key, out acc) : _mutable!.Accounts.TryGetValue(key, out acc);

    public bool HasSelfDestruct(HashedKey<Address> key)
        => _isSorted ? _merged!.HasSelfDestruct(key) : _mutable!.SelfDestructedStorageAddresses.TryGetValue(key, out bool _);

    public bool TryGetStorage(HashedKey<(Address, UInt256)> key, out SlotValue? value)
        => _isSorted ? _merged!.TryGetStorage(key, out value) : _mutable!.Storages.TryGetValue(key, out value);

    public bool TryGetStateNode(HashedKey<TreePath> key, [NotNullWhen(true)] out TrieNode? node)
        => _isSorted ? _merged!.TryGetStateNode(key, out node) : _mutable!.StateNodes.TryGetValue(key, out node!);

    public bool TryGetStorageNode(HashedKey<(Hash256, TreePath)> key, [NotNullWhen(true)] out TrieNode? node)
        => _isSorted ? _merged!.TryGetStorageNode(key, out node) : _mutable!.StorageNodes.TryGetValue(key, out node!);

    protected override void CleanUp()
    {
        if (_isSorted) _resourcePool.ReturnMergedSnapshotContent(_usage, _merged!);
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

    public readonly ConcurrentDictionary<HashedKey<TreePath>, TrieNode> StateNodes = new();
    public readonly ConcurrentDictionary<HashedKey<(Hash256, TreePath)>, TrieNode> StorageNodes = new();

    public void Reset()
    {
        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kvp in StateNodes) kvp.Value.PrunePersistedRecursively(1);
        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kvp in StorageNodes) kvp.Value.PrunePersistedRecursively(1);

        Accounts.NoResizeClear();
        Storages.NoResizeClear();
        SelfDestructedStorageAddresses.NoResizeClear();
        StateNodes.NoResizeClear();
        StorageNodes.NoResizeClear();
    }

    public long EstimateMemory() =>
        // ConcurrentDictionary entry overhead ~48 bytes for Accounts/Storages/SelfDestruct
        Accounts.Count * 172 +                         // Key (12B: ref 8B + hash 4B) + Value ref (8B) + CD overhead (48) + Account object (~104B)
            Storages.Count * 136 +                         // Key (44B: addr ref 8B + UInt256 32B + hash 4B) + Value (40B SlotValue?) + CD overhead (48) + Value ref (4B)
            SelfDestructedStorageAddresses.Count * 64 +    // Key (12B: ref 8B + hash 4B) + Value (4B) + CD overhead (48)
            StateNodes.Count * (NodeSizeEstimate + 76) +   // Key (40B: TreePath 36B + hash 4B) + Value ref (8B) + dictionary overhead (28) + TrieNode
            StorageNodes.Count * (NodeSizeEstimate + 84);  // Key (48B: Hash256 ref 8B + TreePath 36B + hash 4B) + Value ref (8B) + dictionary overhead (28) + TrieNode

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
