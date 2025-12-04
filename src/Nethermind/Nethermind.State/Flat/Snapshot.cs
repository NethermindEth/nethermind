// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Trie;

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
    ObjectPool<SnapshotContent> pool
) : RefCountingDisposable
{
    private Dictionary<MemoryType, long>? _memory = null; // The memory changes, so we use this as a smoewhat estimate so it make some seense
    public Dictionary<MemoryType, long> EstimateMemory() => _memory ??= content.EstimateMemory();

    public StateId From => from;
    public StateId To => to;
    public IEnumerable<KeyValuePair<AddressAsKey, Account?>> Accounts => content.Accounts;
    public IEnumerable<KeyValuePair<AddressAsKey, bool>> SelfDestructedStorageAddresses => content.SelfDestructedStorageAddresses;
    public IEnumerable<KeyValuePair<(AddressAsKey, UInt256), byte[]?>> Storages => content.Storages;
    public IEnumerable<KeyValuePair<(Hash256AsKey, TreePath), TrieNode>> TrieNodes => content.TrieNodes;
    public IEnumerable<(Hash256AsKey, TreePath)> TrieNodeKeys => content.TrieNodes.Keys;
    public int AccountsCount => content.Accounts.Count;
    public int StoragesCount => content.Storages.Count;
    public int TrieNodesCount => content.TrieNodes.Count;

    public bool TryGetAccount(AddressAsKey key, out Account acc)
    {
        return content.Accounts.TryGetValue(key, out acc);
    }

    public bool HasSelfDestruct(Address address)
    {
        return content.SelfDestructedStorageAddresses.TryGetValue(address, out var _);
    }

    public bool TryGetStorage(Address address, in UInt256 index, out byte[] value)
    {
        return content.Storages.TryGetValue((address, index), out value);
    }

    public bool TryGetTrieNodes(Hash256 address, in TreePath path, out TrieNode node)
    {
        return content.TrieNodes.TryGetValue((address, path), out node);
    }

    protected override void CleanUp()
    {
        pool.Return(content);
    }

    public bool TryAcquire()
    {
        return TryAcquireLease();
    }
}

public record SnapshotContent(
    // They dont actually need to be concurrent, but its makes commit fast by just passing the whole content.
    ConcurrentDictionary<AddressAsKey, Account?> Accounts,
    ConcurrentDictionary<(AddressAsKey, UInt256), byte[]?> Storages,

    // Bool is true if this is a new account also
    ConcurrentDictionary<AddressAsKey, bool> SelfDestructedStorageAddresses,
    ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> TrieNodes
) {
    public void Reset()
    {
        Accounts.Clear();
        Storages.Clear();
        SelfDestructedStorageAddresses.Clear();
        TrieNodes.Clear();
    }

    public Dictionary<MemoryType, long> EstimateMemory()
    {
        Dictionary<MemoryType, long> result = new Dictionary<MemoryType, long>(){
            { MemoryType.Account, Accounts.Count },
            { MemoryType.Storage, Storages.Count },
            { MemoryType.StorageBytes, Storages.Sum((kv) => kv.Value?.Length ?? 0) },
            { MemoryType.SelfDestructedAddress, SelfDestructedStorageAddresses.Count },
            { MemoryType.TrieNodes, TrieNodes.Count },
            { MemoryType.TrieNodesBytes, TrieNodes.Sum((kv) => kv.Value.GetMemorySize(false)) },
        };

        // I'm just winging it here.
        result[MemoryType.TotalBytes]
            = result[MemoryType.Account] * 40 +
              result[MemoryType.Storage] * 48 +
              result[MemoryType.StorageBytes] +
              result[MemoryType.SelfDestructedAddress] * 40 +
              result[MemoryType.TrieNodes] + 48 +
              result[MemoryType.TrieNodesBytes];

        return result;
    }
}

public enum MemoryType
{
    Account,
    Storage,
    StorageBytes,
    SelfDestructedAddress,
    TrieNodes,
    TrieNodesBytes,
    TotalBytes
}
