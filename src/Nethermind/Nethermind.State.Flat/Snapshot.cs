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
    ResourcePool resourcePool,
    ResourcePool.Usage usage
) : RefCountingDisposable
{
    private Dictionary<MemoryType, long>? _memory = null; // The memory changes, so we use this as a smoewhat estimate so it make some seense
    public Dictionary<MemoryType, long> EstimateMemory() => _memory ??= content.EstimateMemory();
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
    public int TrieNodesCount => content.StorageNodes.Count;
    public long DebugLease => _leases.Value;
    public SnapshotContent Content => content;

    public bool TryGetAccount(AddressAsKey key, out Account? acc)
    {
        return content.Accounts.TryGetValue(key, out acc);
    }

    public bool HasSelfDestruct(Address address)
    {
        return content.SelfDestructedStorageAddresses.TryGetValue(address, out var _);
    }

    public bool TryGetStorage(Address address, in UInt256 index, out SlotValue? value)
    {
        return content.Storages.TryGetValue((address, index), out value);
    }

    public bool TryGetStateNode(in TreePath path, [NotNullWhen(true)] out TrieNode? node)
    {
        return content.StateNodes.TryGetValue(path, out node);
    }

    public bool TryGetStorageNode(Hash256 address, in TreePath path, [NotNullWhen(true)] out TrieNode? node)
    {
        return content.StorageNodes.TryGetValue((address, path), out node);
    }

    protected override void CleanUp()
    {
        resourcePool.ReturnSnapshotContent(usage, content);
    }

    public bool TryAcquire()
    {
        return TryAcquireLease();
    }
}

public sealed class SnapshotContent() : IDisposable, IResettable
{
    // They dont actually need to be concurrent, but its makes commit fast by just passing the whole content.
    public readonly ConcurrentDictionary<AddressAsKey, Account?> Accounts = new();
    public readonly ConcurrentDictionary<(AddressAsKey, UInt256), SlotValue?> Storages = new();

    // Bool is true if this is a new account also
    public readonly ConcurrentDictionary<AddressAsKey, bool> SelfDestructedStorageAddresses = new();

    // Use of a separate dictionary just for state have a small but measurable impact
    public readonly ConcurrentDictionary<TreePath, TrieNode> StateNodes = new();

    public readonly ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> StorageNodes = new();

    public void Reset()
    {
        foreach (var kv in StateNodes) kv.Value.PrunePersistedRecursively(1);
        foreach (var kv in StorageNodes) kv.Value.PrunePersistedRecursively(1);

        Accounts.NoResizeClear();
        Storages.NoResizeClear();
        SelfDestructedStorageAddresses.NoResizeClear();
        StateNodes.NoResizeClear();
        StorageNodes.NoResizeClear();
    }

    public Dictionary<MemoryType, long> EstimateMemory()
    {
        Dictionary<MemoryType, long> result = new Dictionary<MemoryType, long>(){
            { MemoryType.Account, Accounts.Count },
            { MemoryType.Storage, Storages.Count },
            { MemoryType.StorageBytes, Storages.Count * 40 },
            { MemoryType.SelfDestructedAddress, SelfDestructedStorageAddresses.Count },
            { MemoryType.StateNodes, StateNodes.Count },
            { MemoryType.StateNodesBytes, StateNodes.Count * 700 }, // Just estimate
            { MemoryType.StorageNodes, StorageNodes.Count },
            { MemoryType.StorageNodesBytes, StorageNodes.Count * 700 },
        };

        // I'm just winging it here.
        result[MemoryType.TotalBytes]
            = result[MemoryType.Account] * 40 +
              result[MemoryType.Storage] * 48 +
              result[MemoryType.StorageBytes] +
              result[MemoryType.SelfDestructedAddress] * 40 +
              result[MemoryType.StateNodes] * 40 +
              result[MemoryType.StateNodesBytes] +
              result[MemoryType.StorageNodes] * 48 +
              result[MemoryType.StorageNodesBytes];

        return result;
    }

    public void Dispose()
    {
    }
}

public enum MemoryType
{
    Account,
    Storage,
    StorageBytes,
    SelfDestructedAddress,
    StateNodes,
    StateNodesBytes,
    StorageNodes,
    StorageNodesBytes,
    TotalBytes,
    Count,
}

public record MemoryTypeMetric(MemoryType MemoryType) : IMetricLabels
{
    private static FrozenDictionary<MemoryType, string> Names = Enum.GetValues<MemoryType>().ToDictionary((k) => k, (k) => k.ToString())
        .ToFrozenDictionary();

    public string[] Labels => [Names[MemoryType]];
}
