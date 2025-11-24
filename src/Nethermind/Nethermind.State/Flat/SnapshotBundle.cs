// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

// Reversed order so that its easy to add new KnownState.
// TODO: We can skip the reverse
public class SnapshotBundle(ArrayPoolList<Snapshot> knownStates, IPersistenceReader persistenceReader) : IDisposable
{
    Dictionary<Address, StorageSnapshotBundle> _loadedAccounts = new();
    Dictionary<Address, Account> _changedAccounts = new();
    Dictionary<TreePath, TrieNode> _changedNodes = new();
    public int SnapshotCount => knownStates.Count;

    public bool TryGetAccount(Address address, out Account? acc)
    {
        if (_changedAccounts.TryGetValue(address, out acc)) return true;

        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].Accounts.TryGetValue(address, out var value))
            {
                acc = value.NewValue;
                return true;
            }
        }

        if (persistenceReader.TryGetAccount(address, out acc))
        {
            return true;
        }

        acc = null;
        return false;
    }

    public bool TryGetSlot(Address address, in UInt256 index, out byte[] value)
    {
        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].Storages.TryGetValue((address, index), out value)) return true;
            if (knownStates[i].Accounts.TryGetValue(address, out var accountInfo))
            {
                if (accountInfo.HasSelfDestruct)
                {
                    value = null;
                    return true;
                }
            }
        }

        return persistenceReader.TryGetSlot(address, index, out value);
    }

    public bool TryFindNode(in TreePath path, out TrieNode node)
    {
        if (_changedNodes.TryGetValue(path, out node))
        {
            return true;
        }

        return TryFindNode(null, path, out node);
    }

    public bool TryFindNode(Hash256 address, in TreePath path, out TrieNode node)
    {
        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].TrieNodes.TryGetValue((address, path), out node))
            {
                return true;
            }
        }

        node = null;
        return false;
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return persistenceReader.TryLoadRlp(null, path, hash, flags);
    }

    public void SetStateNode(in TreePath path, TrieNode newNode)
    {
        _changedNodes[path] = newNode;
    }

    public void ApplyStateChanges(Dictionary<Address, Account> changedValues)
    {
        foreach (var kv in changedValues)
        {
            _changedAccounts[kv.Key] = kv.Value;
        }
    }

    public void HintAccountRead(Address address, Account? account)
    {
        _changedAccounts[address] = account;
    }

    public Snapshot CollectAndApplyKnownState()
    {
        Dictionary<Address, AccountSnapshotInfo> accounts = new();
        foreach (var kv in _changedAccounts)
        {
            accounts[kv.Key] = new AccountSnapshotInfo(kv.Value, false);
        }

        Dictionary<(Hash256, TreePath), TrieNode> nodes = new();
        foreach (var kv in _changedNodes)
        {
            nodes[(null, kv.Key)] = kv.Value;
        }

        Dictionary<(Address, UInt256), byte[]> storages = new ();

        foreach (var gatheredCacheStorage in _loadedAccounts)
        {
            bool hasSelfDestruct = gatheredCacheStorage.Value
                .CollectAndApplyKnownState(
                    storages,
                    nodes);
            accounts[gatheredCacheStorage.Key].HasSelfDestruct = hasSelfDestruct;
        }

        var knownState = new Snapshot()
        {
            Accounts = accounts,
            Storages = storages,
            TrieNodes = nodes
        };

        knownStates.Add(knownState);

        _changedAccounts = new();
        _changedNodes = new();
        _loadedAccounts.Clear();

        return knownState;
    }

    public StorageSnapshotBundle GatherStorageCache(Address address)
    {
        if (_loadedAccounts.TryGetValue(address, out var acc))
        {
            return acc;
        }

        StorageSnapshotBundle cache = new StorageSnapshotBundle(address, this);
        _loadedAccounts[address] = cache;
        return cache;
    }

    public Snapshot CompactToKnownState()
    {
        Dictionary<Address, AccountSnapshotInfo> accounts = new Dictionary<Address, AccountSnapshotInfo>();
        Dictionary<(Address, UInt256), byte[]> storages = new Dictionary<(Address, UInt256), byte[]>();
        Dictionary<(Hash256, TreePath), TrieNode> nodes = new Dictionary<(Hash256, TreePath), TrieNode>();

        if (knownStates.Count == 0) return new Snapshot(
            new StateId(-1, ValueKeccak.EmptyTreeHash), new StateId(-1, ValueKeccak.EmptyTreeHash),
            new Dictionary<Address, AccountSnapshotInfo>(),
            new Dictionary<(Address, UInt256), byte[]>(),
            new Dictionary<(Hash256, TreePath), TrieNode>()
        );

        if (knownStates.Count == 1) return knownStates[0];

        StateId to = knownStates[^1].To;
        StateId from = knownStates[1].From;

        for (int i = 0; i < knownStates.Count; i++)
        {
            var knownState = knownStates[i];
            foreach (var knownStateAccount in knownState.Accounts)
            {
                Address address = knownStateAccount.Key;
                accounts[address] = knownStateAccount.Value;

                // Clear
                if (knownStateAccount.Value.HasSelfDestruct)
                {
                    foreach (var kv in storages)
                    {
                        if (kv.Key.Item1 == address)
                        {
                            storages.Remove(kv.Key);
                        }
                    }

                    Hash256 accountHash = address.ToAccountPath.ToCommitment();
                    foreach (var kv in nodes)
                    {
                        if (kv.Key.Item1 == accountHash)
                        {
                            nodes.Remove(kv.Key);
                        }
                    }
                }
            }

            foreach (var knownStateStorage in knownState.Storages)
            {
                storages[knownStateStorage.Key] = knownStateStorage.Value;
            }

            foreach (var storageNodes in knownState.TrieNodes)
            {
                nodes[storageNodes.Key] = storageNodes.Value;
            }
        }

        return new Snapshot(
            from,
            to,
            accounts,
            storages,
            nodes);
    }

    public void Dispose()
    {
        foreach (var gatheredCacheStorage in _loadedAccounts)
        {
            gatheredCacheStorage.Value.Dispose();
        }
    }
}

public class StorageSnapshotBundle(Address address, SnapshotBundle bundle)
{
    Dictionary<UInt256, byte[]> _changedSlots = new();
    Dictionary<TreePath, TrieNode> _changedNodes = new();
    internal Hash256 _addressHash = address.ToAccountPath.ToCommitment();
    bool _hasSelfDestruct = false;

    public bool TryGet(in UInt256 index, out byte[]? value)
    {
        if (_hasSelfDestruct)
        {
            value = null;
            return true;
        }
        if (_changedSlots.TryGetValue(index, out value))
        {
            return true;
        }

        return bundle.TryGetSlot(address, index, out value);
    }

    public bool TryFindNode(in TreePath path, out TrieNode value)
    {
        if (_hasSelfDestruct)
        {
            value = null;
            return true;
        }

        if (_changedNodes.TryGetValue(path, out value))
        {
            return true;
        }

        return bundle.TryFindNode(_addressHash, path, out value);
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return bundle.TryLoadRlp(in path, hash, flags);
    }

    public bool CollectAndApplyKnownState(
        Dictionary<(Address, UInt256), byte[]> storages,
        Dictionary<(Hash256, TreePath), TrieNode> nodes
    )
    {
        foreach (var kv in _changedSlots)
        {
            storages[(address, kv.Key)] = kv.Value;
        }

        foreach (var kv in _changedNodes)
        {
            nodes[(_addressHash, kv.Key)] = kv.Value;
        }

        _changedSlots.Clear();
        _changedNodes.Clear();
        bool hadSelfDestruct = _hasSelfDestruct;
        _hasSelfDestruct = false;
        return hadSelfDestruct;
    }

    public void SetNode(TreePath path, TrieNode node)
    {
        _changedNodes[path] = node;
    }

    public void Set(UInt256 slot, byte[] value)
    {
        _changedSlots[slot] = value;
    }

    public void Dispose()
    {
    }
}
