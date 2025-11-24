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
public class SnapshotBundle(ArrayPoolList<Snapshot> knownStates, IPersistence.IPersistenceReader persistenceReader) : IDisposable
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
            if (knownStates[i].Accounts.TryGetValue(address, out acc))
            {
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

    public int DetermineSelfDestructStateIdx(Address address)
    {
        ValueHash256 accountPath = address.ToAccountPath;
        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            // TODO: This can be optimized  away
            if (knownStates[i].SelfDestructedStorages.Contains(accountPath))
            {
                return i;
            }
        }

        return -1;
    }

    public bool TryGetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out byte[] value)
    {
        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].Storages.TryGetValue((address, index), out value)) return true;

            if (i <= selfDestructStateIdx)
            {
                value = null;
                return true;
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

        return TryFindNode(null, path, -1, out node);
    }

    public bool TryFindNode(Hash256? address, in TreePath path, int selfDestructStateIdx, out TrieNode node)
    {
        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].TrieNodes.TryGetValue((address, path), out node))
            {
                return true;
            }

            if (i <= selfDestructStateIdx)
            {
                node = null;
                return false;
            }
        }

        node = null;
        return false;
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return persistenceReader.TryLoadRlp(null, path, hash, flags);
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return persistenceReader.TryLoadRlp(address, path, hash, flags);
    }

    public void SetStateNode(in TreePath path, TrieNode newNode)
    {
        _changedNodes[path] = newNode;
    }

    public void ApplyStateChanges(Dictionary<AddressAsKey, Account> changedValues)
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
        Dictionary<Hash256, Address> addressHashes = new();
        Dictionary<Address, Account> accounts = new();
        HashSet<ValueHash256> selfDestructedAccounts = new();
        HashSet<Address> selfDestructedAccountAddresses = new();
        foreach (var kv in _changedAccounts)
        {
            addressHashes[kv.Key.ToAccountPath.ToCommitment()] = kv.Key;
            accounts[kv.Key] = kv.Value;
        }

        Dictionary<(Hash256, TreePath), TrieNode> nodes = new();
        foreach (var kv in _changedNodes)
        {
            nodes[(null, kv.Key)] = kv.Value;
        }

        Dictionary<(Address, UInt256), byte[]> storages = new ();

        foreach (var gatheredCacheStorage in _loadedAccounts)
        {
            (bool hasSelfDestruct, bool hasChange) = gatheredCacheStorage.Value
                .CollectAndApplyKnownState(
                    storages,
                    nodes);
            if (!hasChange) continue;

            if (!accounts.ContainsKey(gatheredCacheStorage.Key))
            {
                if (!TryGetAccount(gatheredCacheStorage.Key, out var account))
                {
                    Console.Error.WriteLine($"Cannot get account on storage {gatheredCacheStorage.Key} {hasSelfDestruct}");
                }

                accounts[gatheredCacheStorage.Key] = account;
            }
            if (hasSelfDestruct) selfDestructedAccounts.Add(gatheredCacheStorage.Key.ToAccountPath);
            if (hasSelfDestruct) selfDestructedAccountAddresses.Add(gatheredCacheStorage.Key);
        }

        var knownState = new Snapshot()
        {
            Accounts = accounts,
            SelfDestructedStorages = selfDestructedAccounts,
            SelfDestructedStorageAddresses = selfDestructedAccountAddresses,
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
        if (knownStates.Count == 0) return new Snapshot(
            new StateId(-1, ValueKeccak.EmptyTreeHash), new StateId(-1, ValueKeccak.EmptyTreeHash),
            new Dictionary<Address, Account>(),
            new Dictionary<(Address, UInt256), byte[]>(),
            new HashSet<ValueHash256>(),
            new HashSet<Address>(),
            new Dictionary<(Hash256, TreePath), TrieNode>()
        );

        Dictionary<Address, Account> accounts = new Dictionary<Address, Account>();
        Dictionary<(Address, UInt256), byte[]> storages = new Dictionary<(Address, UInt256), byte[]>();
        HashSet<ValueHash256> selfDestructedStorages = new();
        HashSet<Address> selfDestructedStorageAddresses = new();
        Dictionary<(Hash256, TreePath), TrieNode> nodes = new Dictionary<(Hash256, TreePath), TrieNode>();

        if (knownStates.Count == 1) return knownStates[0];

        StateId to = knownStates[^1].To;
        StateId from = knownStates[0].From;

        for (int i = 0; i < knownStates.Count; i++)
        {
            var knownState = knownStates[i];
            foreach (var knownStateAccount in knownState.Accounts)
            {
                Address address = knownStateAccount.Key;
                accounts[address] = knownStateAccount.Value;
            }

            foreach (Address address in knownState.SelfDestructedStorageAddresses)
            {
                selfDestructedStorageAddresses.Add(address);

                // Clear
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

            foreach (ValueHash256 hash in knownState.SelfDestructedStorages)
            {
                selfDestructedStorages.Add(hash);
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
            selfDestructedStorages,
            selfDestructedStorageAddresses,
            nodes);
    }

    public void Dispose()
    {
        foreach (var gatheredCacheStorage in _loadedAccounts)
        {
            gatheredCacheStorage.Value.Dispose();
        }
        knownStates.Dispose();
    }
}

public class StorageSnapshotBundle(Address address, SnapshotBundle bundle)
{
    Dictionary<UInt256, byte[]> _changedSlots = new();
    Dictionary<TreePath, TrieNode> _changedNodes = new();
    internal Hash256 _addressHash = address.ToAccountPath.ToCommitment();
    bool _hasSelfDestruct = false;
    private int _selfDestructKnownStateIdx = bundle.DetermineSelfDestructStateIdx(address);

    public bool TryGet(in UInt256 index, out byte[]? value)
    {
        if (_changedSlots.TryGetValue(index, out value))
        {
            return true;
        }

        if (_hasSelfDestruct)
        {
            value = null;
            return true;
        }

        return bundle.TryGetSlot(address, index, _selfDestructKnownStateIdx, out value);
    }

    public bool TryFindNode(in TreePath path, out TrieNode value)
    {
        if (_changedNodes.TryGetValue(path, out value))
        {
            return true;
        }

        if (_hasSelfDestruct)
        {
            value = null;
            return true;
        }

        return bundle.TryFindNode(_addressHash, path, _selfDestructKnownStateIdx, out value);
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return bundle.TryLoadRlp(_addressHash, in path, hash, flags);
    }

    public (bool hasSelfDesruct, bool hasChange) CollectAndApplyKnownState(
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

        bool hadSelfDestruct = _hasSelfDestruct;
        bool hasChange = hadSelfDestruct || _changedNodes.Count > 0 || _changedSlots.Count > 0;
        _changedSlots.Clear();
        _changedNodes.Clear();
        _hasSelfDestruct = false;
        return (hadSelfDestruct, hasChange);
    }

    public void SetNode(TreePath path, TrieNode node)
    {
        _changedNodes[path] = node;
    }

    public void Set(UInt256 slot, byte[] value)
    {
        _changedSlots[slot] = value;
    }

    public void SelfDestruct()
    {
        _hasSelfDestruct = true;
        _changedSlots.Clear();
        _changedNodes.Clear();
    }

    public void Dispose()
    {
    }
}
