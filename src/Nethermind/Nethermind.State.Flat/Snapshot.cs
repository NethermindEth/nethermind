// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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
/// Snapshot are written keys between state From to state To
/// </summary>
/// <param name="From"></param>
/// <param name="To"></param>
/// <param name="Accounts"></param>
/// <param name="Storages"></param>
public class Snapshot(
    StateId from,
    StateId to,
    SnapshotContent content,
    IResourcePool resourcePool,
    ResourcePool.Usage usage
) : RefCountingDisposable
{
    public long EstimateMemory() => content.EstimateMemory();
    public ResourcePool.Usage Usage => usage;

    public StateId From => from;
    public StateId To => to;
    public IEnumerable<KeyValuePair<HashedKey<AddressAsKey>, Account?>> Accounts => content.Accounts;
    public IEnumerable<KeyValuePair<HashedKey<AddressAsKey>, bool>> SelfDestructedStorageAddresses => content.SelfDestructedStorageAddresses;
    public IEnumerable<KeyValuePair<HashedKey<(AddressAsKey, UInt256)>, SlotValue?>> Storages => content.Storages;
    public IEnumerable<KeyValuePair<HashedKey<(Hash256AsKey, TreePath)>, TrieNode>> StorageNodes => content.StorageNodes;
    public IEnumerable<(Hash256AsKey, TreePath)> StorageTrieNodeKeys => content.StorageNodes.Keys.Select(k => k.Key);
    public IEnumerable<KeyValuePair<HashedKey<TreePath>, TrieNode>> StateNodes => content.StateNodes;
    public IEnumerable<TreePath> StateNodeKeys => content.StateNodes.Keys.Select(k => k.Key);
    public int AccountsCount => content.Accounts.Count;
    public int StoragesCount => content.Storages.Count;
    public int StateNodesCount => content.StateNodes.Count;
    public int StorageNodesCount => content.StorageNodes.Count;
    public SnapshotContent Content => content;

    public bool TryGetAccount(HashedKey<AddressAsKey> key, out Account? acc) => content.Accounts.TryGetValue(key, out acc);

    public bool HasSelfDestruct(HashedKey<AddressAsKey> key) => content.SelfDestructedStorageAddresses.TryGetValue(key, out bool _);

    public bool TryGetStorage(HashedKey<(AddressAsKey, UInt256)> key, out SlotValue? value) => content.Storages.TryGetValue(key, out value);

    public bool TryGetStateNode(HashedKey<TreePath> key, [NotNullWhen(true)] out TrieNode? node) => content.StateNodes.TryGetValue(key, out node);

    public bool TryGetStorageNode(HashedKey<(Hash256AsKey, TreePath)> key, [NotNullWhen(true)] out TrieNode? node) => content.StorageNodes.TryGetValue(key, out node);

    protected override void CleanUp() => resourcePool.ReturnSnapshotContent(usage, content);

    public bool TryAcquire() => TryAcquireLease();
}

public sealed class SnapshotContent : IDisposable, IResettable
{
    private const int NodeSizeEstimate = 650; // Counting the node size one by one has a notable overhead. So we use estimate.

    // ConcurrentDictionary: lock-free reads, best read latency for accounts/slots
    public readonly ConcurrentDictionary<HashedKey<AddressAsKey>, Account?> Accounts = new();
    public readonly ConcurrentDictionary<HashedKey<(AddressAsKey, UInt256)>, SlotValue?> Storages = new();
    public readonly ConcurrentDictionary<HashedKey<AddressAsKey>, bool> SelfDestructedStorageAddresses = new();

    // ShardedDictionary: sharded locks, best parallel write throughput for trie nodes
    public readonly ShardedDictionary<HashedKey<TreePath>, TrieNode> StateNodes = new();
    public readonly ShardedDictionary<HashedKey<(Hash256AsKey, TreePath)>, TrieNode> StorageNodes = new();

    public void Reset()
    {
        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in StateNodes) kv.Value.PrunePersistedRecursively(1);
        foreach (KeyValuePair<HashedKey<(Hash256AsKey, TreePath)>, TrieNode> kv in StorageNodes) kv.Value.PrunePersistedRecursively(1);

        Accounts.NoResizeClear();
        Storages.NoResizeClear();
        SelfDestructedStorageAddresses.NoResizeClear();
        StateNodes.NoResizeClear();
        StorageNodes.NoResizeClear();
    }

    public long EstimateMemory()
    {
        // ConcurrentDictionary entry overhead ~48 bytes for Accounts/Storages/SelfDestruct
        return
            Accounts.Count * 168 +                         // Key (8B) + Value ref (8B) + CD overhead (48) + Account object (~104B)
            Storages.Count * 128 +                         // Key (40B) + Value (40B SlotValue?) + CD overhead (48)
            SelfDestructedStorageAddresses.Count * 60 +    // Key (8B) + Value (4B) + CD overhead (48)
            StateNodes.Count * (NodeSizeEstimate + 72) +   // Key (36B) + Value ref (8B) + dictionary overhead (28) + TrieNode
            StorageNodes.Count * (NodeSizeEstimate + 80);  // Key (44B) + Value ref (8B) + dictionary overhead (28) + TrieNode
    }

    /// <summary>
    /// Estimates memory for compacted snapshots, counting only dictionary overhead + keys + value-type values.
    /// Does not count reference type values (Account and TrieNode) as they are already accounted for
    /// by non-compacted snapshots (compacted snapshots share these references with the original snapshots).
    /// </summary>
    public long EstimateCompactedMemory()
    {
        // ConcurrentDictionary entry overhead ~48 bytes
        // Reference type values (Account, TrieNode) not counted - already accounted by non-compacted snapshot
        return
            Accounts.Count * 64 +                          // Key (8B) + Value ref (8B) + CD overhead (48)
            Storages.Count * 128 +                         // Key (40B) + Value (40B SlotValue?) + CD overhead (48)
            SelfDestructedStorageAddresses.Count * 60 +    // Key (8B) + Value (4B) + CD overhead (48)
            StateNodes.Count * 72 +                        // Key (36B TreePath) + Value ref (8B) + dictionary overhead (28)
            StorageNodes.Count * 80;                       // Key (44B) + Value ref (8B) + dictionary overhead (28)
    }

    public void DisableLocks()
    {
        StateNodes.DisableLock();
        StorageNodes.DisableLock();
    }

    public void EnableLocks()
    {
        StateNodes.EnableLock();
        StorageNodes.EnableLock();
    }

    public void Dispose()
    {
    }
}
