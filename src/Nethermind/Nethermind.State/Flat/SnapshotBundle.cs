// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat;

// Reversed order so that its easy to add new KnownState.
// TODO: We can skip the reverse
public class SnapshotBundle : IDisposable
{
    private Dictionary<AddressAsKey, StorageSnapshotBundle> _loadedAccounts;

    private SnapshotContent _currentPooledContent;
    private Dictionary<AddressAsKey, Account> _changedAccounts;
    private ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> _changedNodes; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<(AddressAsKey, UInt256), byte[]> _changedSlots; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<AddressAsKey, bool> _selfDestructedAccountAddresses;

    public int SnapshotCount => _knownStates.Count;

    internal static Histogram _snapshotBundleTimer = Prometheus.Metrics.CreateHistogram("snapshot_bundle_timer", "timer",
        new HistogramConfiguration()
        {
            LabelNames = ["part", "is_prewarmer"],
            Buckets = Histogram.PowersOfTenDividedBuckets(5, 10, 5)
        });

    private Histogram.Child _snapshotBundleTimerKnownStates;
    private Histogram.Child _snapshotBundleTimerPersistence;
    private Histogram.Child _snapshotBundleTimerPersistenceNull;
    private Histogram.Child _snapshotBundleTimerKnownStatesStorage;
    private Histogram.Child _snapshotBundleTimerPersistenceStorage;
    private Histogram.Child _snapshotBundleTimerPersistenceNullStorage;
    private Histogram.Child _loadTriePersistence;
    private Histogram.Child _loadTriePersistenceStorage;
    private Histogram.Child _loadTrie;

    private static Counter _loadTrieCacheHit = Prometheus.Metrics.CreateCounter("load_trie_cache_hit", "", "hit", "type");
    private Counter.Child _loadTrieCacheHitStateHit = _loadTrieCacheHit.WithLabels("hit", "state");
    private Counter.Child _loadTrieCacheHitStateMiss = _loadTrieCacheHit.WithLabels("miss", "state");
    private Counter.Child _loadTrieCacheHitStorageHit = _loadTrieCacheHit.WithLabels("hit", "storage");
    private Counter.Child _loadTrieCacheHitStorageMiss = _loadTrieCacheHit.WithLabels("miss", "storage");
    private readonly ArrayPoolList<Snapshot> _knownStates;
    private readonly IPersistence.IPersistenceReader _persistenceReader;
    private readonly TrieNodeCache _trieNodeCache;
    private readonly ObjectPool<SnapshotContent> _contentPool;
    private bool _isPrewarmer;

    public SnapshotBundle(ArrayPoolList<Snapshot> knownStates,
        IPersistence.IPersistenceReader persistenceReader,
        TrieNodeCache trieNodeCache,
        ObjectPool<SnapshotContent> contentPool,
        bool isPrewarmer = false)
    {
        _knownStates = knownStates;
        _persistenceReader = persistenceReader;
        _trieNodeCache = trieNodeCache;
        _contentPool = contentPool;
        _isPrewarmer = isPrewarmer;

        _loadedAccounts = new Dictionary<AddressAsKey, StorageSnapshotBundle>();

        _currentPooledContent = contentPool.Get();
        ExpandCurrentPooledContent();

        _snapshotBundleTimerKnownStates = _snapshotBundleTimer.WithLabels("known_states", isPrewarmer.ToString());
        _snapshotBundleTimerPersistence = _snapshotBundleTimer.WithLabels("persistence", isPrewarmer.ToString());
        _snapshotBundleTimerPersistenceNull = _snapshotBundleTimer.WithLabels("persistence_null", isPrewarmer.ToString());
        _snapshotBundleTimerKnownStatesStorage = _snapshotBundleTimer.WithLabels("known_states_storage", isPrewarmer.ToString());
        _snapshotBundleTimerPersistenceStorage = _snapshotBundleTimer.WithLabels("persistence_storage", isPrewarmer.ToString());
        _snapshotBundleTimerPersistenceNullStorage = _snapshotBundleTimer.WithLabels("persistence_null_storage", isPrewarmer.ToString());
        _loadTriePersistence = _snapshotBundleTimer.WithLabels("load_trie_persistence", isPrewarmer.ToString());
        _loadTriePersistenceStorage = _snapshotBundleTimer.WithLabels("load_trie_persistence_storage", isPrewarmer.ToString());
        _loadTrie = _snapshotBundleTimer.WithLabels("load_trie", isPrewarmer.ToString());
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
        _snapshotBundleTimerKnownStates = _snapshotBundleTimer.WithLabels("known_states", _isPrewarmer.ToString());
        _snapshotBundleTimerPersistence = _snapshotBundleTimer.WithLabels("persistence", _isPrewarmer.ToString());
        _snapshotBundleTimerPersistenceNull = _snapshotBundleTimer.WithLabels("persistence_null", _isPrewarmer.ToString());
        _snapshotBundleTimerKnownStatesStorage = _snapshotBundleTimer.WithLabels("known_states_storage", _isPrewarmer.ToString());
        _snapshotBundleTimerPersistenceStorage = _snapshotBundleTimer.WithLabels("persistence_storage", _isPrewarmer.ToString());
        _snapshotBundleTimerPersistenceNullStorage = _snapshotBundleTimer.WithLabels("persistence_null_storage", _isPrewarmer.ToString());
        _loadTriePersistence = _snapshotBundleTimer.WithLabels("load_trie_persistence", _isPrewarmer.ToString());
        _loadTriePersistenceStorage = _snapshotBundleTimer.WithLabels("load_trie_persistence_storage", _isPrewarmer.ToString());
        _loadTrie = _snapshotBundleTimer.WithLabels("load_trie", _isPrewarmer.ToString());
    }

    public bool TryGetAccountInMemory(Address address, out Account? acc)
    {
        if (_changedAccounts.TryGetValue(address, out acc)) return true;

        AddressAsKey key = address;

        long sw = Stopwatch.GetTimestamp();
        for (int i = _knownStates.Count - 1; i >= 0; i--)
        {
            if (_knownStates[i].TryGetAccount(key, out acc))
            {
                _snapshotBundleTimerKnownStates.Observe(Stopwatch.GetTimestamp() - sw);
                return true;
            }
        }
        _snapshotBundleTimerKnownStates.Observe(Stopwatch.GetTimestamp() - sw);

        acc = null;
        return false;
    }

    public bool TryGetAccount(Address address, out Account? acc)
    {
        if (TryGetAccountInMemory(address, out acc)) return true;

        long sw = Stopwatch.GetTimestamp();
        if (_persistenceReader.TryGetAccount(address, out acc))
        {
            if (acc == null)
            {
                _snapshotBundleTimerPersistenceNull.Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _snapshotBundleTimerPersistence.Observe(Stopwatch.GetTimestamp() - sw);
            }
            return true;
        }

        acc = null;
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
        long sw = Stopwatch.GetTimestamp();
        for (int i = _knownStates.Count - 1; i >= 0; i--)
        {
            if (_knownStates[i].TryGetStorage(address, index, out value)) return true;

            if (i <= selfDestructStateIdx)
            {
                _snapshotBundleTimerKnownStatesStorage.Observe(Stopwatch.GetTimestamp() - sw);
                value = null;
                return true;
            }
        }
        _snapshotBundleTimerKnownStatesStorage.Observe(Stopwatch.GetTimestamp() - sw);

        sw = Stopwatch.GetTimestamp();
        if (_persistenceReader.TryGetSlot(address, index, out value))
        {
            if (value == null)
            {
                _snapshotBundleTimerPersistenceNullStorage.Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _snapshotBundleTimerPersistenceStorage.Observe(Stopwatch.GetTimestamp() - sw);
            }

            return true;
        }

        return false;
    }

    public bool TryFindNode(Hash256AsKey addr, in TreePath path, Hash256 hash, out TrieNode node)
    {
        if (TryGetChangedNode(addr, in path, hash, out node))
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
        long sw = Stopwatch.GetTimestamp();
        for (int i = _knownStates.Count - 1; i >= 0; i--)
        {
            if (_knownStates[i].TryGetTrieNodes(address, path, out node))
            {
                _loadTrie.Observe(Stopwatch.GetTimestamp() - sw);
                return true;
            }

            if (i <= selfDestructStateIdx)
            {
                _loadTrie.Observe(Stopwatch.GetTimestamp() - sw);
                node = null;
                return false;
            }
        }

        var res = _trieNodeCache.TryGet(address, path, hash, out node);
        if (res)
        {
            if (address is null)
            {
                _loadTrieCacheHitStateHit.Inc();
            }
            else
            {
                _loadTrieCacheHitStorageHit.Inc();
            }
        }
        else
        {
            if (address is null)
            {
                _loadTrieCacheHitStateMiss.Inc();
            }
            else
            {
                _loadTrieCacheHitStorageMiss.Inc();
            }
        }

        _loadTrie.Observe(Stopwatch.GetTimestamp() - sw);
        return res;
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        long sw = Stopwatch.GetTimestamp();
        var res =  _persistenceReader.TryLoadRlp(null, path, hash, flags);
        _loadTriePersistence.Observe(Stopwatch.GetTimestamp() - sw);
        return res;
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        long sw = Stopwatch.GetTimestamp();
        var res = _persistenceReader.TryLoadRlp(address, path, hash, flags);
        if (address is null)
        {
            _loadTriePersistence.Observe(Stopwatch.GetTimestamp() - sw);
        }
        else
        {
            _loadTriePersistenceStorage.Observe(Stopwatch.GetTimestamp() - sw);
        }
        return res;
    }

    public void SetStateNode(in TreePath path, TrieNode newNode)
    {
        SetNode(null, path, newNode);
    }

    public void SetNode(Hash256AsKey addr, in TreePath path, TrieNode newNode)
    {
        _changedNodes[(addr, path)] = newNode;
    }

    public void ApplyStateChanges(Dictionary<AddressAsKey, Account> changedValues)
    {
        foreach (var kv in changedValues)
        {
            _changedAccounts[kv.Key] = kv.Value;
        }
    }

    public bool HintAccountRead(Address address, Account? account)
    {
        return _changedAccounts.TryAdd(address, account);
    }

    public Snapshot CollectAndApplyKnownState(StateId from, StateId to)
    {
        var knownState = new Snapshot(
            from: from,
            to: to,
            content: _currentPooledContent,
            pool: _contentPool);

        knownState.AcquireLease(); // For this bundle

        _knownStates.Add(knownState);

        _currentPooledContent = _contentPool.Get();
        ExpandCurrentPooledContent();

        foreach (var gatheredCacheStorage in _loadedAccounts)
        {
            gatheredCacheStorage.Value.Dispose();
        }
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

        for (int i = 0; i < _knownStates.Count; i++)
        {
            var knownState = _knownStates[i];
            foreach (var knownStateAccount in knownState.Accounts)
            {
                Address address = knownStateAccount.Key;
                accounts[address] = knownStateAccount.Value;
            }

            foreach (KeyValuePair<AddressAsKey, bool> addrK in knownState.SelfDestructedStorageAddresses)
            {
                var address = addrK.Key;
                selfDestructedStorageAddresses[address] = true;

                // Clear
                foreach (var kv in storages)
                {
                    if (kv.Key.Item1 == address.Value)
                    {
                        storages.Remove(kv.Key, out _);
                    }
                }

                Hash256 accountHash = address.Value.ToAccountPath.ToCommitment();
                foreach (var kv in nodes)
                {
                    if (kv.Key.Item1 == accountHash)
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
        foreach (var gatheredCacheStorage in _loadedAccounts)
        {
            gatheredCacheStorage.Value.Dispose();
        }
        foreach (Snapshot knownState in _knownStates)
        {
            knownState.Dispose();
        }
        _knownStates.Dispose();

        // Null them in case unexpected mutation
        _changedSlots = null;
        _changedAccounts = null;
        _changedNodes = null;
        _selfDestructedAccountAddresses = null;

        _contentPool.Return(_currentPooledContent);
    }

    public void SelfDestruct(Address address, Hash256AsKey addressHash)
    {
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

        _selfDestructedAccountAddresses.TryAdd(address, true);
    }

    public bool TryGetChangedSlot(AddressAsKey address, in UInt256 index, out byte[] value)
    {
        return _changedSlots.TryGetValue((address, index), out value);
    }

    public void SetChangedSlot(AddressAsKey address, in UInt256 index, byte[] value)
    {
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
        bundle.SelfDestruct(address, _addressHash);
    }

    public void Dispose()
    {
    }
}
