// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
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
    public IEnumerable<KeyValuePair<AddressAsKey, Account?>> Accounts => content.Accounts;
    public IEnumerable<KeyValuePair<AddressAsKey, bool>> SelfDestructedStorageAddresses => content.SelfDestructedStorageAddresses;
    public IEnumerable<KeyValuePair<(AddressAsKey, UInt256), SlotValue?>> Storages => content.Storages;
    public IEnumerable<KeyValuePair<(Hash256AsKey, TreePath), TrieNode>> StorageNodes => content.StorageNodes;
    public IEnumerable<(Hash256AsKey, TreePath)> StorageTrieNodeKeys => content.StorageNodes.Keys;
    public IEnumerable<KeyValuePair<TreePath, TrieNode>> StateNodes => content.StateNodes;
    public IEnumerable<TreePath> StateNodeKeys => content.StateNodes.Keys;
    public int AccountsCount => content.Accounts.Count;
    public int StoragesCount => content.Storages.Count;
    public int StateNodesCount => content.StateNodes.Count;
    public int StorageNodesCount => content.StorageNodes.Count;
    public SnapshotContent Content => content;

    public bool TryGetAccount(AddressAsKey key, out Account? acc) => content.Accounts.TryGetValue(key, out acc);

    public bool HasSelfDestruct(Address address) => content.SelfDestructedStorageAddresses.TryGetValue(address, out bool _);

    public bool TryGetStorage(Address address, in UInt256 index, out SlotValue? value) => content.Storages.TryGetValue((address, index), out value);

    public bool TryGetStateNode(in TreePath path, [NotNullWhen(true)] out TrieNode? node) => content.StateNodes.TryGetValue(path, out node);

    public bool TryGetStorageNode(Hash256 address, in TreePath path, [NotNullWhen(true)] out TrieNode? node) => content.StorageNodes.TryGetValue((address, path), out node);

    protected override void CleanUp() => resourcePool.ReturnSnapshotContent(usage, content);

    public bool TryAcquire() => TryAcquireLease();
}

public sealed class SnapshotContent : IDisposable, IResettable
{
    private const int NodeSizeEstimate = 650; // Counting the node size one by one has a notable overhead. So we use estimate.

    // They dont actually need to be concurrent, but it makes commit fast by just passing the whole content.
    public readonly ConcurrentDictionary<AddressAsKey, Account?> Accounts = new();
    public readonly ConcurrentDictionary<(AddressAsKey, UInt256), SlotValue?> Storages = new();

    // Bool is true if this is a new account also
    public readonly ConcurrentDictionary<AddressAsKey, bool> SelfDestructedStorageAddresses = new();

    // Use of a separate dictionary just for state has a small but measurable impact
    public readonly ConcurrentDictionary<TreePath, TrieNode> StateNodes = new();

    public readonly ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> StorageNodes = new();

    public void Reset()
    {
        foreach (KeyValuePair<TreePath, TrieNode> kv in StateNodes) kv.Value.PrunePersistedRecursively(1);
        foreach (KeyValuePair<(Hash256AsKey, TreePath), TrieNode> kv in StorageNodes) kv.Value.PrunePersistedRecursively(1);

        Accounts.NoResizeClear();
        Storages.NoResizeClear();
        SelfDestructedStorageAddresses.NoResizeClear();
        StateNodes.NoResizeClear();
        StorageNodes.NoResizeClear();
    }

    public long EstimateMemory()
    {
        // ConcurrentDictionary entry overhead ~48 bytes, includes Account object (~104 bytes)
        return
            Accounts.Count * 168 +                         // Key (8B) + Value ref (8B) + concurrent dictionary overhead (48) + Account object (~104B)
            Storages.Count * 128 +                         // Key (40B) + Value (40B SlotValue?) + concurrent dictionary overhead (48)
            SelfDestructedStorageAddresses.Count * 60 +    // Key (8B) + Value (4B) + concurrent dictionary overhead (48)
            StateNodes.Count * (NodeSizeEstimate + 92) +   // Key (36B) + Value ref (8B) + concurrent dictionary overhead (48) + TrieNode
            StorageNodes.Count * (NodeSizeEstimate + 100); // Key (44B) + Value ref (8B) + concurrent dictionary overhead (48) + TrieNode
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
            Accounts.Count * 64 +                          // Key (8B) + Value ref (8B) + concurrent dictionary overhead (48)
            Storages.Count * 128 +                         // Key (40B) + Value (40B SlotValue?) + concurrent dictionary overhead (48)
            SelfDestructedStorageAddresses.Count * 60 +    // Key (8B) + Value (4B) + concurrent dictionary overhead (48)
            StateNodes.Count * 92 +                        // Key (36B TreePath) + Value ref (8B) + concurrent dictionary overhead (48)
            StorageNodes.Count * 100;                      // Key (44B) + Value ref (8B) + concurrent dictionary overhead (48)
    }

    public void Dispose()
    {
    }
}

