// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

// Reversed order so that its easy to add new KnownState.
// TODO: We can skip the reverse
public class SnapshotBundle : IDisposable
{
    private Dictionary<AddressAsKey, StorageSnapshotBundle> _loadedContractStorages;

    private SnapshotContent _currentPooledContent;
    private Dictionary<AddressAsKey, Account> _changedAccounts;
    private ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> _changedNodes; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<(AddressAsKey, UInt256), byte[]> _changedSlots; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<AddressAsKey, bool> _selfDestructedAccountAddresses;

    private readonly bool _isReadOnly;

    public int SnapshotCount => _knownStates.Count;

    private ArrayPoolList<Snapshot> _knownStates;
    private readonly IPersistence.IPersistenceReader _persistenceReader;
    private readonly TrieNodeCache _trieNodeCache;
    private readonly ObjectPool<SnapshotContent> _contentPool;
    private bool _isPrewarmer;
    private bool _isDisposed;

    public SnapshotBundle(ArrayPoolList<Snapshot> knownStates,
        IPersistence.IPersistenceReader persistenceReader,
        TrieNodeCache trieNodeCache,
        ObjectPool<SnapshotContent> contentPool,
        bool isReadOnly = false,
        bool isPrewarmer = false)
    {
        _knownStates = knownStates;
        _persistenceReader = persistenceReader;
        _trieNodeCache = trieNodeCache;
        _contentPool = contentPool;
        _isPrewarmer = isPrewarmer;
        _isReadOnly = isReadOnly;

        _loadedContractStorages = new Dictionary<AddressAsKey, StorageSnapshotBundle>();

        if (!_isReadOnly)
        {
            _currentPooledContent = contentPool.Get();
            ExpandCurrentPooledContent();
        }
    }

    private void ExpandCurrentPooledContent()
    {
        _changedAccounts = _currentPooledContent.Accounts;
        _changedSlots = _currentPooledContent.Storages;
        _changedNodes = _currentPooledContent.TrieNodes;
        _selfDestructedAccountAddresses = _currentPooledContent.SelfDestructedStorageAddresses;
    }

    public void SetPrewarmer()
    {
        _isPrewarmer = true;
    }

    public bool TryGetAccountInMemory(Address address, out Account? acc)
    {
        if (!_isReadOnly && _changedAccounts.TryGetValue(address, out acc)) return true;

        AddressAsKey key = address;

        for (int i = _knownStates.Count - 1; i >= 0; i--)
        {
            if (_knownStates[i].TryGetAccount(key, out acc))
            {
                return true;
            }
        }

        acc = null;
        return false;
    }

    public bool TryGetAccount(Address address, out Account? acc)
    {
        if (TryGetAccountInMemory(address, out acc)) return true;

        if (_persistenceReader.TryGetAccount(address, out acc))
        {
            return true;
        }

        return false;
    }

    public int DetermineSelfDestructStateIdx(Address address)
    {
        for (int i = _knownStates.Count - 1; i >= 0; i--)
        {
            if (_knownStates[i].HasSelfDestruct(address))
            {
                return i;
            }
        }

        return -1;
    }

    public bool TryGetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out byte[] value)
    {
        for (int i = _knownStates.Count - 1; i >= 0; i--)
        {
            if (_knownStates[i].TryGetStorage(address, index, out value)) return true;

            if (i <= selfDestructStateIdx)
            {
                value = null;
                return true;
            }
        }

        if (_persistenceReader.TryGetSlot(address, index, out value))
        {
            return true;
        }

        return false;
    }

    public bool TryFindNode(Hash256AsKey addr, in TreePath path, Hash256 hash, out TrieNode node)
    {
        if (!_isReadOnly && TryGetChangedNode(addr, in path, hash, out node))
        {
            return true;
        }

        return TryFindNode(addr, path, hash, -1, out node);
    }

    public bool TryGetChangedNode(Hash256AsKey addr, in TreePath path, Hash256 hash, out TrieNode node)
    {
        return _changedNodes.TryGetValue((addr, path), out node);
    }

    public bool TryFindNode(Hash256? address, in TreePath path, Hash256 hash, int selfDestructStateIdx, out TrieNode node)
    {
        if (_isDisposed)
        {
            node = null;
            return false;
        }

        for (int i = _knownStates.Count - 1; i >= 0; i--)
        {
            if (_knownStates[i].TryGetTrieNodes(address, path, out node))
            {
                return true;
            }

            if (i <= selfDestructStateIdx)
            {
                node = null;
                return false;
            }
        }

        return _trieNodeCache.TryGet(address, path, hash, out node);
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        if (_isDisposed) return null;
        return _persistenceReader.TryLoadRlp(address, path, hash, flags);
    }

    public void SetNode(Hash256AsKey addr, in TreePath path, TrieNode newNode)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        if (_isDisposed) return;
        _changedNodes[(addr, path)] = newNode;
    }

    public void ApplyStateChanges(Dictionary<AddressAsKey, Account> changedValues)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        foreach (var kv in changedValues)
        {
            _changedAccounts[kv.Key] = kv.Value;
        }
    }

    public bool HintAccountRead(Address address, Account? account)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        return _changedAccounts.TryAdd(address, account);
    }

    public Snapshot CollectAndApplyKnownState(StateId from, StateId to)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        var knownState = new Snapshot(
            from: from,
            to: to,
            content: _currentPooledContent,
            pool: _contentPool);

        knownState.AcquireLease(); // For this bundle

        _knownStates.Add(knownState);

        _currentPooledContent = _contentPool.Get();
        ExpandCurrentPooledContent();

        foreach (var gatheredCacheStorage in _loadedContractStorages)
        {
            gatheredCacheStorage.Value.Dispose();
        }
        _loadedContractStorages.Clear();

        return knownState;
    }

    public StorageSnapshotBundle GatherStorageCache(Address address)
    {
        ref var snapshotBundle = ref CollectionsMarshal.GetValueRefOrAddDefault(_loadedContractStorages, address, out bool exists);
        if (!exists)
        {
            snapshotBundle = new StorageSnapshotBundle(address, this);
        }

        return snapshotBundle;
    }

    public Snapshot CompactToKnownState(ObjectPool<SnapshotContent> contentPool)
    {
        if (_knownStates.Count == 0)
            return new Snapshot(
                new StateId(-1, ValueKeccak.EmptyTreeHash), new StateId(-1, ValueKeccak.EmptyTreeHash),
                content: contentPool.Get(),
                pool: contentPool);

        SnapshotContent content = contentPool.Get();

        Dictionary<AddressAsKey, Account> accounts = content.Accounts;
        ConcurrentDictionary<(AddressAsKey, UInt256), byte[]> storages = content.Storages;
        ConcurrentDictionary<AddressAsKey, bool> selfDestructedStorageAddresses = content.SelfDestructedStorageAddresses;
        ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> nodes = content.TrieNodes;

        if (_knownStates.Count == 1) return _knownStates[0];

        StateId to = _knownStates[^1].To;
        StateId from = _knownStates[0].From;
        HashSet<Address> addressToClear = new HashSet<Address>();
        HashSet<Hash256AsKey> addressHashToClear = new HashSet<Hash256AsKey>();


        for (int i = 0; i < _knownStates.Count; i++)
        {
            var knownState = _knownStates[i];
            foreach (var knownStateAccount in knownState.Accounts)
            {
                Address address = knownStateAccount.Key;
                accounts[address] = knownStateAccount.Value;
            }

            addressToClear.Clear();
            addressHashToClear.Clear();

            foreach (KeyValuePair<AddressAsKey, bool> addrK in knownState.SelfDestructedStorageAddresses)
            {
                var address = addrK.Key;
                var isNewAccount = addrK.Value;
                selfDestructedStorageAddresses[address] = isNewAccount;

                if (!isNewAccount)
                {
                    addressToClear.Add(address);
                    addressHashToClear.Add(address.Value.ToAccountPath.ToCommitment());
                }
            }

            if (addressToClear.Count > 0)
            {
                // Clear
                foreach (var kv in storages)
                {
                    if (addressToClear.Contains(kv.Key.Item1))
                    {
                        storages.Remove(kv.Key, out _);
                    }
                }

                foreach (var kv in nodes)
                {
                    if (addressHashToClear.Contains(kv.Key.Item1))
                    {
                        nodes.Remove(kv.Key, out _);
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
            content: content,
            pool: contentPool);
    }

    public void Dispose()
    {
        _isDisposed = true;
        foreach (var gatheredCacheStorage in _loadedContractStorages)
        {
            gatheredCacheStorage.Value.Dispose();
        }
        foreach (Snapshot knownState in _knownStates)
        {
            knownState.Dispose();
        }
        _knownStates.Dispose();

        // Null them in case unexpected mutation from trie warmer
        _knownStates = null;
        _changedSlots = null;
        _changedAccounts = null;
        _changedNodes = null;
        _selfDestructedAccountAddresses = null;

        _persistenceReader.Dispose();

        if (!_isReadOnly) _contentPool.Return(_currentPooledContent);
    }

    public void Clear(Address address, Hash256AsKey addressHash)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        foreach (var kv in _changedNodes)
        {
            if (kv.Key.Item1.Value == addressHash)
            {
                _changedNodes.TryRemove(kv.Key, out TrieNode _);
            }
        }

        foreach (var kv in _changedSlots)
        {
            if (kv.Key.Item1.Value == address)
            {
                _changedSlots.TryRemove(kv.Key, out byte[] _);
            }
        }

        bool isNewAccount = false;
        if (TryGetAccount(address, out Account? account))
        {
            // So... a clear is always sent even on new account. This makes is a minor optimization as
            // it skip persistence, but probably need to make sure it does not send it at all in the first place.
            isNewAccount = account == null;
        }
        _selfDestructedAccountAddresses.TryAdd(address, isNewAccount);
    }

    public bool TryGetChangedSlot(AddressAsKey address, in UInt256 index, out byte[] value)
    {
        if (_isReadOnly)
        {
            value = null;
            return false;
        }

        return _changedSlots.TryGetValue((address, index), out value);
    }

    public void SetChangedSlot(AddressAsKey address, in UInt256 index, byte[] value)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        _changedSlots[(address, index)] = value;
    }

    public bool HasChangedSlot(AddressAsKey address, in UInt256 index)
    {
        return _changedSlots.ContainsKey((address, index));
    }
}

public class StorageSnapshotBundle(Address address, SnapshotBundle bundle)
{
    internal Hash256 _addressHash = address.ToAccountPath.ToCommitment();

    bool _hasSelfDestruct = false;
    private int _selfDestructKnownStateIdx = bundle.DetermineSelfDestructStateIdx(address);

    public bool TryGet(in UInt256 index, out byte[]? value)
    {
        if (bundle.TryGetChangedSlot(address, index, out value))
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

    public bool TryFindNode(in TreePath path, Hash256 hash, out TrieNode value)
    {
        if (bundle.TryGetChangedNode(_addressHash, path, hash, out value))
        {
            return true;
        }

        if (_hasSelfDestruct)
        {
            value = null;
            return true;
        }

        return bundle.TryFindNode(_addressHash, path, hash, _selfDestructKnownStateIdx, out value);
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return bundle.TryLoadRlp(_addressHash, in path, hash, flags);
    }

    public void SetNode(TreePath path, TrieNode node)
    {
        bundle.SetNode(_addressHash, path, node);
    }

    public void Set(UInt256 slot, byte[] value)
    {
        bundle.SetChangedSlot(address, slot, value);
    }

    public bool HintGet(UInt256 slot, byte[] value)
    {
        if (bundle.HasChangedSlot(address, slot)) return false;
        bundle.SetChangedSlot(address, slot, value);
        return true;
    }

    public void SelfDestruct()
    {
        _hasSelfDestruct = true;
        bundle.Clear(address, _addressHash);
    }

    public void Dispose()
    {
    }
}
