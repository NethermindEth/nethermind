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
public class SnapshotBundle(
    ArrayPoolList<Snapshot> knownStates,
    IPersistence.IPersistenceReader persistenceReader,
    TrieNodeCache trieNodeCache,
    ObjectPool<SnapshotContent> contentPool,
    bool isPrewarmer = false
) : IDisposable
{
    Dictionary<AddressPrefixAsKey, StorageSnapshotBundle> _loadedAccounts = new();
    Dictionary<AddressPrefixAsKey, Account> _changedAccounts = new();
    ConcurrentDictionary<TreePath, TrieNode> _changedNodes = new(); // Bulkset can get nodes concurrently
    public int SnapshotCount => knownStates.Count;

    internal static Histogram _snapshotBundleTimer = Prometheus.Metrics.CreateHistogram("snapshot_bundle_timer", "timer",
        new HistogramConfiguration()
        {
            LabelNames = ["part", "is_prewarmer"],
            Buckets = Histogram.PowersOfTenDividedBuckets(5, 10, 5)
        });

    private Histogram.Child _snapshotBundleTimerKnownStates = _snapshotBundleTimer.WithLabels("known_states", isPrewarmer.ToString());
    private Histogram.Child _snapshotBundleTimerPersistence = _snapshotBundleTimer.WithLabels("persistence", isPrewarmer.ToString());
    private Histogram.Child _snapshotBundleTimerPersistenceNull = _snapshotBundleTimer.WithLabels("persistence_null", isPrewarmer.ToString());
    private Histogram.Child _snapshotBundleTimerKnownStatesStorage = _snapshotBundleTimer.WithLabels("known_states_storage", isPrewarmer.ToString());
    private Histogram.Child _snapshotBundleTimerPersistenceStorage = _snapshotBundleTimer.WithLabels("persistence_storage", isPrewarmer.ToString());
    private Histogram.Child _snapshotBundleTimerPersistenceNullStorage = _snapshotBundleTimer.WithLabels("persistence_null_storage", isPrewarmer.ToString());
    private Histogram.Child _loadTriePersistence = _snapshotBundleTimer.WithLabels("load_trie_persistence", isPrewarmer.ToString());
    private Histogram.Child _loadTriePersistenceStorage = _snapshotBundleTimer.WithLabels("load_trie_persistence_storage", isPrewarmer.ToString());
    private Histogram.Child _loadTrie = _snapshotBundleTimer.WithLabels("load_trie", isPrewarmer.ToString());

    private static Counter _loadTrieCacheHit = Prometheus.Metrics.CreateCounter("load_trie_cache_hit", "", "hit", "type");
    private Counter.Child _loadTrieCacheHitStateHit = _loadTrieCacheHit.WithLabels("hit", "state");
    private Counter.Child _loadTrieCacheHitStateMiss = _loadTrieCacheHit.WithLabels("miss", "state");
    private Counter.Child _loadTrieCacheHitStorageHit = _loadTrieCacheHit.WithLabels("hit", "storage");
    private Counter.Child _loadTrieCacheHitStorageMiss = _loadTrieCacheHit.WithLabels("miss", "storage");

    public void SetPrewarmer()
    {
        isPrewarmer = true;
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

    public bool TryGetAccountInMemory(Address address, out Account? acc)
    {
        if (_changedAccounts.TryGetValue(address, out acc)) return true;

        AddressPrefixAsKey key = address;

        long sw = Stopwatch.GetTimestamp();
        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].TryGetAccount(key, out acc))
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
        if (persistenceReader.TryGetAccount(address, out acc))
        {
            _snapshotBundleTimerPersistence.Observe(Stopwatch.GetTimestamp() - sw);
            return true;
        }

        _snapshotBundleTimerPersistenceNull.Observe(Stopwatch.GetTimestamp() - sw);
        acc = null;
        return false;
    }

    public int DetermineSelfDestructStateIdx(Address address)
    {
        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].HasSelfDestruct(address))
            {
                return i;
            }
        }

        return -1;
    }

    public bool TryGetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out byte[] value)
    {
        long sw = Stopwatch.GetTimestamp();
        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].TryGetStorage(address, index, out value)) return true;

            if (i <= selfDestructStateIdx)
            {
                _snapshotBundleTimerKnownStatesStorage.Observe(Stopwatch.GetTimestamp() - sw);
                value = null;
                return true;
            }
        }
        _snapshotBundleTimerKnownStatesStorage.Observe(Stopwatch.GetTimestamp() - sw);

        sw = Stopwatch.GetTimestamp();
        // TODO: This iis wrong
        var res = persistenceReader.TryGetSlot(address, index, out value);
        if (value == null)
        {
            _snapshotBundleTimerPersistenceStorage.Observe(Stopwatch.GetTimestamp() - sw);
        }
        else
        {
            _snapshotBundleTimerPersistenceNullStorage.Observe(Stopwatch.GetTimestamp() - sw);
        }

        return res;
    }

    public bool TryFindNode(in TreePath path, Hash256 hash, out TrieNode node)
    {
        if (_changedNodes.TryGetValue(path, out node))
        {
            return true;
        }

        return TryFindNode(null, path, hash, -1, out node);
    }

    public bool TryFindNode(Hash256? address, in TreePath path, Hash256 hash, int selfDestructStateIdx, out TrieNode node)
    {
        long sw = Stopwatch.GetTimestamp();
        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].TryGetTrieNodes(address, path, out node))
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

        var res = trieNodeCache.TryGet(address, path, hash, out node);
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
        var res =  persistenceReader.TryLoadRlp(null, path, hash, flags);
        _loadTriePersistence.Observe(Stopwatch.GetTimestamp() - sw);
        return res;
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        long sw = Stopwatch.GetTimestamp();
        var res = persistenceReader.TryLoadRlp(address, path, hash, flags);
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
        _changedNodes[path] = newNode;
    }

    public void ApplyStateChanges(Dictionary<AddressPrefixAsKey, Account> changedValues)
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
        SnapshotContent content = contentPool.Get();
        Dictionary<AddressPrefixAsKey, Account> accounts = content.Accounts;
        HashSet<AddressPrefixAsKey> selfDestructedAccountAddresses = content.SelfDestructedStorageAddresses;
        foreach (var kv in _changedAccounts)
        {
            accounts[kv.Key] = kv.Value;
        }

        Dictionary<(Hash256PrefixAsKey, TreePath), TrieNode> nodes = content.TrieNodes;
        foreach (var kv in _changedNodes)
        {
            nodes[(null, kv.Key)] = kv.Value;
        }

        Dictionary<(AddressPrefixAsKey, UInt256), byte[]> storages = content.Storages;

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
            if (hasSelfDestruct) selfDestructedAccountAddresses.Add(gatheredCacheStorage.Key);
        }

        var knownState = new Snapshot(
            from: from,
            to: to,
            content: content,
            pool: contentPool);

        knownState.AcquireLease(); // For this bundle
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

    public Snapshot CompactToKnownState(ObjectPool<SnapshotContent> contentPool)
    {
        if (knownStates.Count == 0)
            return new Snapshot(
                new StateId(-1, ValueKeccak.EmptyTreeHash), new StateId(-1, ValueKeccak.EmptyTreeHash),
                content: contentPool.Get(),
                pool: contentPool);

        SnapshotContent content = contentPool.Get();

        Dictionary<AddressPrefixAsKey, Account> accounts = content.Accounts;
        Dictionary<(AddressPrefixAsKey, UInt256), byte[]> storages = content.Storages;
        HashSet<AddressPrefixAsKey> selfDestructedStorageAddresses = content.SelfDestructedStorageAddresses;
        Dictionary<(Hash256PrefixAsKey, TreePath), TrieNode> nodes = content.TrieNodes;

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
                        storages.Remove(kv.Key, out _);
                    }
                }

                Hash256 accountHash = address.ToAccountPath.ToCommitment();
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
        foreach (Snapshot knownState in knownStates)
        {
            knownState.Dispose();
        }
        knownStates.Dispose();
    }
}

public class StorageSnapshotBundle(Address address, SnapshotBundle bundle)
{
    Dictionary<UInt256, byte[]> _changedSlots = new();
    ConcurrentDictionary<TreePath, TrieNode> _changedNodes = new(); // Trie store may set nodes concurrently
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

    public bool TryFindNode(in TreePath path, Hash256 hash, out TrieNode value)
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

        return bundle.TryFindNode(_addressHash, path, hash, _selfDestructKnownStateIdx, out value);
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return bundle.TryLoadRlp(_addressHash, in path, hash, flags);
    }

    public (bool hasSelfDesruct, bool hasChange) CollectAndApplyKnownState(
        Dictionary<(AddressPrefixAsKey, UInt256), byte[]> storages,
        Dictionary<(Hash256PrefixAsKey, TreePath), TrieNode> nodes
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

    public bool HintGet(UInt256 slot, byte[] value)
    {
        return _changedSlots.TryAdd(slot, value);
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
