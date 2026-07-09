// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Collections;
using Nethermind.Trie;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Flat;

/// <summary>The sorted, pooled counterpart of <see cref="SnapshotContent"/> used for compacted snapshots.</summary>
public sealed class SortedSnapshotContent : IDisposable, IResettable
{
    private const int NodeSizeEstimate = 650;

    private readonly SortedMergeDictionary<HashedKey<Address>, Account?> _accounts = new();
    private readonly SortedMergeDictionary<HashedKey<(Address, UInt256)>, SlotValue?> _storages = new();
    private readonly SortedMergeDictionary<HashedKey<Address>, bool> _selfDestructedStorageAddresses = new();
    private readonly SortedMergeDictionary<HashedKey<TreePath>, TrieNode> _stateNodes = new();
    private readonly SortedMergeDictionary<HashedKey<(Hash256, TreePath)>, TrieNode> _storageNodes = new();

    internal SortedMergeDictionary<HashedKey<Address>, Account?> SortedAccounts => _accounts;
    internal SortedMergeDictionary<HashedKey<(Address, UInt256)>, SlotValue?> SortedStorages => _storages;
    internal SortedMergeDictionary<HashedKey<Address>, bool> SortedSelfDestructs => _selfDestructedStorageAddresses;
    internal SortedMergeDictionary<HashedKey<TreePath>, TrieNode> SortedStateNodes => _stateNodes;
    internal SortedMergeDictionary<HashedKey<(Hash256, TreePath)>, TrieNode> SortedStorageNodes => _storageNodes;

    public IEnumerable<KeyValuePair<HashedKey<Address>, Account?>> Accounts => _accounts;
    public IEnumerable<KeyValuePair<HashedKey<Address>, bool>> SelfDestructedStorageAddresses => _selfDestructedStorageAddresses;
    public IEnumerable<KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?>> Storages => _storages;
    public IEnumerable<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>> StorageNodes => _storageNodes;
    public IEnumerable<KeyValuePair<HashedKey<TreePath>, TrieNode>> StateNodes => _stateNodes;

    public int AccountsCount => _accounts.Count;
    public int StoragesCount => _storages.Count;
    public int StateNodesCount => _stateNodes.Count;
    public int StorageNodesCount => _storageNodes.Count;

    public bool TryGetAccount(HashedKey<Address> key, out Account? acc) => _accounts.TryGetValue(key, out acc);
    public bool HasSelfDestruct(HashedKey<Address> key) => _selfDestructedStorageAddresses.TryGetValue(key, out bool _);
    public bool TryGetStorage(HashedKey<(Address, UInt256)> key, out SlotValue? value) => _storages.TryGetValue(key, out value);
    public bool TryGetStateNode(HashedKey<TreePath> key, [NotNullWhen(true)] out TrieNode? node) => _stateNodes.TryGetValue(key, out node!);
    public bool TryGetStorageNode(HashedKey<(Hash256, TreePath)> key, [NotNullWhen(true)] out TrieNode? node) => _storageNodes.TryGetValue(key, out node!);

    public long EstimateMemory() =>
        _accounts.Count * 172 +
            _storages.Count * 136 +
            _selfDestructedStorageAddresses.Count * 64 +
            _stateNodes.Count * (NodeSizeEstimate + 76) +
            _storageNodes.Count * (NodeSizeEstimate + 84);

    public long EstimateCompactedMemory() =>
        _accounts.Count * 68 +
            _storages.Count * 136 +
            _selfDestructedStorageAddresses.Count * 64 +
            _stateNodes.Count * 76 +
            _storageNodes.Count * 84;

    public void Reset()
    {
        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kvp in _stateNodes) kvp.Value.PrunePersistedRecursively(1);
        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kvp in _storageNodes) kvp.Value.PrunePersistedRecursively(1);

        _accounts.NoResizeClear();
        _storages.NoResizeClear();
        _selfDestructedStorageAddresses.NoResizeClear();
        _stateNodes.NoResizeClear();
        _storageNodes.NoResizeClear();
    }

    public void Dispose()
    {
        _accounts.Dispose();
        _storages.Dispose();
        _selfDestructedStorageAddresses.Dispose();
        _stateNodes.Dispose();
        _storageNodes.Dispose();
    }
}
