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
    public IEnumerable<KeyValuePair<HashedKey<Address>, Account?>> Accounts => content.Accounts;
    public IEnumerable<KeyValuePair<HashedKey<Address>, bool>> SelfDestructedStorageAddresses => content.SelfDestructedStorageAddresses;
    public IEnumerable<KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?>> Storages => content.Storages;
    public IEnumerable<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>> StorageNodes => content.StorageNodes;
    public IEnumerable<(Hash256, TreePath)> StorageTrieNodeKeys => content.StorageNodes.Select(static kvp => kvp.Key.Key);
    public IEnumerable<KeyValuePair<HashedKey<TreePath>, TrieNode>> StateNodes => content.StateNodes;
    public IEnumerable<TreePath> StateNodeKeys => content.StateNodes.Select(static kvp => kvp.Key.Key);
    public int AccountsCount => content.Accounts.Count;
    public int StoragesCount => content.Storages.Count;
    public int StateNodesCount => content.StateNodes.Count;
    public int StorageNodesCount => content.StorageNodes.Count;
    public SnapshotContent Content => content;

    public bool TryGetAccount(HashedKey<Address> key, out Account? acc) => content.Accounts.TryGetValue(key, out acc);

    public bool HasSelfDestruct(HashedKey<Address> key) => content.SelfDestructedStorageAddresses.TryGetValue(key, out bool _);

    public bool TryGetStorage(HashedKey<(Address, UInt256)> key, out SlotValue? value) => content.Storages.TryGetValue(key, out value);

    public bool TryGetStateNode(HashedKey<TreePath> key, [NotNullWhen(true)] out TrieNode? node) => content.StateNodes.TryGetValue(key, out node);

    public bool TryGetStorageNode(HashedKey<(Hash256, TreePath)> key, [NotNullWhen(true)] out TrieNode? node) => content.StorageNodes.TryGetValue(key, out node);

    protected override void CleanUp() => resourcePool.ReturnSnapshotContent(usage, content);

    public bool TryAcquire() => TryAcquireLease();
}

public sealed class SnapshotContent : IDisposable, IResettable
{
    private const int NodeSizeEstimate = 650; // Counting the node size one by one has a notable overhead. So we use estimate.

    // Block-processing contents take bucket-lock writes from the main thread (account hints) and the
    // parallel trie commit; fixed wide lock arrays exceed the growLockArray ceiling (1024) the default
    // ctor converges to, and avoid its per-resize full-lock acquisitions from birth.
    private const int NodeConcurrencyLevel = 2048;
    private const int FlatConcurrencyLevel = 1024;
    private const int InitialCapacity = 1024;

    // ConcurrentDictionary: lock-free reads, best read latency for accounts/slots
    public readonly ConcurrentDictionary<HashedKey<Address>, Account?> Accounts;
    public readonly ConcurrentDictionary<HashedKey<(Address, UInt256)>, SlotValue?> Storages;
    public readonly ConcurrentDictionary<HashedKey<Address>, bool> SelfDestructedStorageAddresses = new();

    public readonly ConcurrentDictionary<HashedKey<TreePath>, TrieNode> StateNodes;
    public readonly ConcurrentDictionary<HashedKey<(Hash256, TreePath)>, TrieNode> StorageNodes;

    public SnapshotContent() : this(forBlockProcessing: false)
    {
    }

    public SnapshotContent(bool forBlockProcessing)
    {
        if (forBlockProcessing)
        {
            Accounts = new(FlatConcurrencyLevel, InitialCapacity);
            Storages = new(FlatConcurrencyLevel, InitialCapacity);
            StateNodes = new(NodeConcurrencyLevel, InitialCapacity);
            StorageNodes = new(NodeConcurrencyLevel, InitialCapacity);
        }
        else
        {
            Accounts = new();
            Storages = new();
            StateNodes = new();
            StorageNodes = new();
        }
    }

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
