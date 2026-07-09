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

/// <summary>
/// The immutable, sorted counterpart of <see cref="SnapshotContent"/> used for compacted snapshots. Each of the
/// five collections is a build-once <see cref="SortedMergeDictionary{TKey,TValue}"/> produced by k-way merging
/// the source snapshots, so lookups are O(1) and enumeration is already in key order (state and storage trie
/// nodes iterate in the exact order the persistence layer would otherwise sort into).
/// </summary>
/// <remarks>
/// Pooled like <see cref="SnapshotContent"/>. Populated once via <see cref="SetContent"/> right after a
/// compaction merge; never mutated afterwards, so no concurrent access guards are needed for reads.
/// </remarks>
public sealed class MergedSnapshotContent : IDisposable, IResettable
{
    private const int NodeSizeEstimate = 650; // Matches SnapshotContent; per-node size is estimated to avoid walking.

    private SortedMergeDictionary<HashedKey<Address>, Account?> _accounts = null!;
    private SortedMergeDictionary<HashedKey<(Address, UInt256)>, SlotValue?> _storages = null!;
    private SortedMergeDictionary<HashedKey<Address>, bool> _selfDestructedStorageAddresses = null!;
    private SortedMergeDictionary<HashedKey<TreePath>, TrieNode> _stateNodes = null!;
    private SortedMergeDictionary<HashedKey<(Hash256, TreePath)>, TrieNode> _storageNodes = null!;

    public void SetContent(
        SortedMergeDictionary<HashedKey<Address>, Account?> accounts,
        SortedMergeDictionary<HashedKey<(Address, UInt256)>, SlotValue?> storages,
        SortedMergeDictionary<HashedKey<Address>, bool> selfDestructedStorageAddresses,
        SortedMergeDictionary<HashedKey<TreePath>, TrieNode> stateNodes,
        SortedMergeDictionary<HashedKey<(Hash256, TreePath)>, TrieNode> storageNodes)
    {
        _accounts = accounts;
        _storages = storages;
        _selfDestructedStorageAddresses = selfDestructedStorageAddresses;
        _stateNodes = stateNodes;
        _storageNodes = storageNodes;
    }

    internal SortedMergeDictionary<HashedKey<Address>, Account?> SortedAccounts => _accounts;
    internal SortedMergeDictionary<HashedKey<(Address, UInt256)>, SlotValue?> SortedStorages => _storages;
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
        if (_stateNodes is not null)
            foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kvp in _stateNodes) kvp.Value.PrunePersistedRecursively(1);
        if (_storageNodes is not null)
            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kvp in _storageNodes) kvp.Value.PrunePersistedRecursively(1);

        // Drop the merged arrays so the pooled shell does not pin them.
        _accounts = null!;
        _storages = null!;
        _selfDestructedStorageAddresses = null!;
        _stateNodes = null!;
        _storageNodes = null!;
    }

    public void Dispose() { }
}
