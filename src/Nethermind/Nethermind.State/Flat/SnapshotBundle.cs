// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.State.Flat;

// Reversed order so that its easy to add new KnownState.
// TODO: We can skip the reverse
public class SnapshotBundle : IDisposable
{
    private Dictionary<AddressAsKey, StorageSnapshotBundle> _loadedContractStorages;

    // Used to solve the problem of how do we prevent the warmer from setting the account when it is being written actively.
    // When a write batch is created, the write lock is entered and sequence id is incremented. Trie warmer is
    // now no longer able to set the accounts until the write lock is exited. After it is exited, the sequence id
    // from before is no longer the same and it no longer match and not applied. The world state scope need to be careful
    // not to use the updated sequence id until the write is complete though...
    // TODO: Check if it is even worth it....
    private ReaderWriterLockSlim _hintLock = new ReaderWriterLockSlim();
    private volatile int _hintSequenceId = 0;

    private SnapshotContent _currentPooledContent;
    private ConcurrentDictionary<AddressAsKey, Account> _changedAccounts;
    private ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> _changedNodes; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<(AddressAsKey, UInt256), byte[]> _changedSlots; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<AddressAsKey, bool> _selfDestructedAccountAddresses;

    public int HintSequenceId => _hintSequenceId;

    private readonly bool _isReadOnly;

    public int SnapshotCount => _knownStates.Count;

    private ArrayPoolList<Snapshot> _knownStates;
    private readonly IPersistence.IPersistenceReader _persistenceReader;
    private readonly TrieNodeCache _trieNodeCache;
    private bool _isPrewarmer;
    private bool _isDisposed;
    private readonly ResourcePool _resourcePool;

    private static Counter _snapshotBundleEvents = Metrics.CreateCounter("snapshot_bundle_evens", "event", "type", "is_prewarmer");
    private Counter.Child _nodeGetChanged;
    private Counter.Child _nodeGetSnapshots;
    private Counter.Child _nodeGetTrieCache;
    private Counter.Child _nodeGetMiss;
    private Counter.Child _nodeGetSelfDestruct;
    private Counter.Child _accountHintWrite;
    private Counter.Child _storageHintWrite;

    public SnapshotBundle(ArrayPoolList<Snapshot> knownStates,
        IPersistence.IPersistenceReader persistenceReader,
        TrieNodeCache trieNodeCache,
        ResourcePool resourcePool,
        bool isReadOnly = false,
        bool isPrewarmer = false)
    {
        _knownStates = knownStates;
        _persistenceReader = persistenceReader;
        _trieNodeCache = trieNodeCache;
        _resourcePool = resourcePool;
        _isPrewarmer = isPrewarmer;
        _isReadOnly = isReadOnly;

        _loadedContractStorages = new Dictionary<AddressAsKey, StorageSnapshotBundle>();
        SetupMetric();

        if (!_isReadOnly)
        {
            _currentPooledContent = resourcePool.GetSnapshotContent();
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
        SetupMetric();
    }

    private void SetupMetric()
    {
        _nodeGetChanged = _snapshotBundleEvents.WithLabels("node_get_changed", _isPrewarmer.ToString());
        _nodeGetSnapshots = _snapshotBundleEvents.WithLabels("node_get_snapshots", _isPrewarmer.ToString());
        _nodeGetTrieCache = _snapshotBundleEvents.WithLabels("node_get_trie_cache", _isPrewarmer.ToString());
        _nodeGetSelfDestruct = _snapshotBundleEvents.WithLabels("node_get_self_destruct", _isPrewarmer.ToString());
        _nodeGetMiss = _snapshotBundleEvents.WithLabels("node_get_miss", _isPrewarmer.ToString());
        _accountHintWrite = _snapshotBundleEvents.WithLabels("account_hint_write", _isPrewarmer.ToString());
        _storageHintWrite = _snapshotBundleEvents.WithLabels("storage_hint_write", _isPrewarmer.ToString());
    }

    private bool TryGetAccountInMemory(Address address, out Account? acc)
    {
        if (!_isReadOnly)
        {
            if (_changedAccounts.TryGetValue(address, out acc)) return true;
        }

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

    public bool TryGetChangedSlot(AddressAsKey address, in UInt256 index, out byte[] value)
    {
        if (!_isReadOnly)
        {
            if (_changedSlots.TryGetValue((address, index), out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    public void SetChangedSlot(AddressAsKey address, in UInt256 index, byte[] value)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        _changedSlots[(address, index)] = value;
    }

    public bool TryFindNode(Hash256AsKey addr, in TreePath path, Hash256 hash, out TrieNode node)
    {
        if (TryGetChangedNode(addr, in path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return true;
        }

        return TryFindNode(addr, path, hash, -1, out node);
    }

    public bool TryGetChangedNode(Hash256AsKey addr, in TreePath path, Hash256 hash, out TrieNode node)
    {
        if (_changedNodes.TryGetValue((addr, path), out node))
        {
            _nodeGetChanged.Inc();
            return true;
        }

        return false;
    }

    public bool TryFindNode(Hash256AsKey? address, in TreePath path, Hash256 hash, int selfDestructStateIdx,
        out TrieNode node)
    {
        if (DoTryFindNode(address, path, hash, selfDestructStateIdx, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return true;
        }

        return false;
    }

    private bool DoTryFindNode(Hash256AsKey? address, in TreePath path, Hash256 hash, int selfDestructStateIdx, out TrieNode node)
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
                _nodeGetSnapshots.Inc();
                return true;
            }

            if (i <= selfDestructStateIdx)
            {
                _nodeGetSelfDestruct.Inc();
                node = null;
                return false;
            }
        }

        if (_trieNodeCache.TryGet(address, path, hash, out node))
        {
            _nodeGetTrieCache.Inc();
            return true;
        }

        _nodeGetMiss.Inc();
        return false;
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        if (_isDisposed) return null;
        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        return _persistenceReader.TryLoadRlp(address, path, hash, flags);
    }

    public void SetNode(Hash256AsKey addr, in TreePath path, TrieNode newNode)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        if (_isDisposed) return;
        _changedNodes[(addr, path)] = newNode;
    }

    public void HintTrieNode(Hash256AsKey addr, in TreePath path, TrieNode newNode)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        if (_isDisposed) return;
        _changedNodes.TryAdd((addr, path), newNode);
    }

    public void ApplyStateChanges(Dictionary<AddressAsKey, Account> changedValues)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        foreach (var kv in changedValues)
        {
            _changedAccounts[kv.Key] = kv.Value;
        }
    }

    public bool HintAccountRead(Address address, Account? account, int sequenceId)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        if (_hintLock.TryEnterReadLock(0))
        {
            try
            {
                if (_hintSequenceId != sequenceId) return false;
                return _changedAccounts.TryAdd(address, account);
            }
            finally
            {
                _hintLock.ExitReadLock();
            }
        }

        return false;
    }

    public bool HintGet(AddressAsKey address, in UInt256 index, int sequenceId, byte[] value)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");

        if (_hintLock.TryEnterReadLock(0))
        {
            try
            {
                if (_hintSequenceId != sequenceId) return false;
                return _changedSlots.TryAdd((address, index), value);
            }
            finally
            {
                _hintLock.ExitReadLock();
            }
        }

        return false;
    }

    public struct WriteScopeExiter(ReaderWriterLockSlim lockc): IDisposable
    {
        public void Dispose()
        {
            lockc.ExitWriteLock();
        }
    }

    public WriteScopeExiter EnterWrites()
    {
        _hintLock.EnterWriteLock();
        Interlocked.Increment(ref _hintSequenceId);
        return new WriteScopeExiter(_hintLock);
    }

    public Snapshot CollectAndApplyKnownState(StateId from, StateId to)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");

        var knownState = new Snapshot(
            from: from,
            to: to,
            content: _currentPooledContent,
            pool: _resourcePool.SnapshotPool);

        knownState.AcquireLease(); // For this bundle

        _knownStates.Add(knownState);

        _currentPooledContent = _resourcePool.GetSnapshotContent();
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

    public Snapshot CompactToKnownState()
    {
        if (_knownStates.Count == 0)
            return new Snapshot(
                new StateId(-1, ValueKeccak.EmptyTreeHash), new StateId(-1, ValueKeccak.EmptyTreeHash),
                content: _resourcePool.GetSnapshotContent(),
                pool: _resourcePool.SnapshotPool);

        SnapshotContent content = _resourcePool.GetCompactedSnapshotPool();

        ConcurrentDictionary<AddressAsKey, Account> accounts = content.Accounts;
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
            pool: _resourcePool.CompactedSnapshotPool);
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

        if (!_isReadOnly) _resourcePool.ReturnSnapshotContent(_currentPooledContent);
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
}
