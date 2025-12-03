// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    private ConcurrentDictionary<(AddressAsKey, UInt256?), bool> _wasPrewarmed;

    // Used to solve the problem of how do we prevent the warmer from setting the account when it is being written actively.
    // When a write batch is created, the write lock is entered and sequence id is incremented. Trie warmer is
    // now no longer able to set the accounts until the write lock is exited. After it is exited, the sequence id
    // from before is no longer the same and it no longer match and not applied. The world state scope need to be careful
    // not to use the updated sequence id until the write is complete though...
    // TODO: Check if it is even worth it.... really, maybe the HintGet's time is not too much of a big deal.
    private ReaderWriterLockSlim _hintLock = new ReaderWriterLockSlim();
    private volatile int _hintSequenceId = 0;

    private SnapshotContent _currentPooledContent;
    private ConcurrentDictionary<AddressAsKey, Account?> _changedAccounts;
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

    private static Histogram _snapshotBundleTimes = Metrics.CreateHistogram("snapshot_bundle_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "type", "is_prewarmer" },
        Buckets = Histogram.PowersOfTenDividedBuckets(2, 12, 5)
    });
    private Histogram.Child _accountPersistenceRead;
    private Histogram.Child _slotPersistenceRead;
    private Histogram.Child _accountPersistenceEmptyRead;
    private Histogram.Child _slotPersistenceEmptyRead;
    private Histogram.Child _loadRlpRead;
    private Histogram.Child _loadRlpReadTrieWarmer;
    private Histogram.Child _loadStorageRlpRead;
    private Histogram.Child _loadStorageRlpReadTrieWarmer;
    private Counter.Child _accountGet;
    private Counter.Child _slotGet;

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
        _wasPrewarmed = new ConcurrentDictionary<(AddressAsKey, UInt256?), bool>();
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
        _accountGet = _snapshotBundleEvents.WithLabels("account_get", _isPrewarmer.ToString());
        _slotGet = _snapshotBundleEvents.WithLabels("slot_get", _isPrewarmer.ToString());

        _accountPersistenceRead = _snapshotBundleTimes.WithLabels("account_persistence", _isPrewarmer.ToString());
        _slotPersistenceRead = _snapshotBundleTimes.WithLabels("slot_persistence", _isPrewarmer.ToString());
        _accountPersistenceEmptyRead = _snapshotBundleTimes.WithLabels("empty_account_persistence", _isPrewarmer.ToString());
        _slotPersistenceEmptyRead = _snapshotBundleTimes.WithLabels("empty_slot_persistence", _isPrewarmer.ToString());
        _loadRlpRead = _snapshotBundleTimes.WithLabels("rlp_read", _isPrewarmer.ToString());
        _loadRlpReadTrieWarmer = _snapshotBundleTimes.WithLabels("rlp_read_trie_warmer", _isPrewarmer.ToString());
        _loadStorageRlpRead = _snapshotBundleTimes.WithLabels("storage_rlp_read", _isPrewarmer.ToString());
        _loadStorageRlpReadTrieWarmer = _snapshotBundleTimes.WithLabels("storage_rlp_read_trie_warmer", _isPrewarmer.ToString());
    }

    public bool TryGetAccount(Address address, out Account? acc)
    {
        if (_isDisposed)
        {
            acc = null;
            return false;
        }

        _accountGet.Inc();
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

        long sw = Stopwatch.GetTimestamp();
        if (_persistenceReader.TryGetAccount(address, out acc))
        {
            if (acc is null)
            {
                _accountPersistenceEmptyRead.Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _accountPersistenceRead.Observe(Stopwatch.GetTimestamp() - sw);
            }
            return true;
        }

        return false;
    }

    public int DetermineSelfDestructStateIdx(Address address)
    {
        if (_selfDestructedAccountAddresses.ContainsKey(address))
        {
            return _knownStates.Count;
        }

        for (int i = _knownStates.Count - 1; i >= 0; i--)
        {
            if (_knownStates[i].HasSelfDestruct(address))
            {
                return i;
            }
        }

        return -1;
    }

    public int GetSelfDestructKnownStateId()
    {
        return _knownStates.Count;
    }

    public bool TryGetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out byte[] value)
    {
        if (_isDisposed)
        {
            value = null;
            return false;
        }

        _slotGet.Inc();

        if (!_isReadOnly)
        {
            if (_changedSlots.TryGetValue((address, index), out value))
            {
                return true;
            }
        }

        if (selfDestructStateIdx == _knownStates.Count)
        {
            _nodeGetSelfDestruct.Inc();
            value = null;
            return true;
        }

        for (int i = _knownStates.Count - 1; i >= 0; i--)
        {
            if (_knownStates[i].TryGetStorage(address, index, out value)) return true;

            if (i <= selfDestructStateIdx)
            {
                value = null;
                return true;
            }
        }

        long sw = Stopwatch.GetTimestamp();
        if (_persistenceReader.TryGetSlot(address, index, out value))
        {
            if (value is null || value.Length == 0 || Bytes.AreEqual(value, StorageTree.ZeroBytes))
            {
                _slotPersistenceEmptyRead.Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _slotPersistenceRead.Observe(Stopwatch.GetTimestamp() - sw);
            }
            return true;
        }

        return false;
    }

    public void SetChangedSlot(AddressAsKey address, in UInt256 index, byte[] value)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        _changedSlots[(address, index)] = value;
    }

    public bool TryFindNode(Hash256AsKey addr, in TreePath path, Hash256 hash, out TrieNode node)
    {
        return TryFindNode(addr, path, hash, -1, out node);
    }

    public bool TryFindNode(Hash256AsKey address, in TreePath path, Hash256 hash, int selfDestructStateIdx,
        out TrieNode node)
    {
        if (_isDisposed)
        {
            node = null;
            return false;
        }

        if (_changedNodes.TryGetValue((address, path), out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            _nodeGetChanged.Inc();
            return true;
        }

        if (selfDestructStateIdx == _knownStates.Count)
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            _nodeGetSelfDestruct.Inc();
            node = null;
            return true;
        }

        for (int i = _knownStates.Count - 1; i >= 0; i--)
        {
            if (_knownStates[i].TryGetTrieNodes(address, path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                _nodeGetSnapshots.Inc();
                return true;
            }

            if (i <= selfDestructStateIdx)
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                _nodeGetSelfDestruct.Inc();
                node = null;
                return true;
            }
        }

        if (_trieNodeCache.TryGet(address, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            _nodeGetTrieCache.Inc();
            return true;
        }

        _nodeGetMiss.Inc();
        return false;
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags, bool isTrieWarmer)
    {
        if (_isDisposed) return null;
        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        long sw = Stopwatch.GetTimestamp();
        var res = _persistenceReader.TryLoadRlp(address, path, hash, flags);
        if (isTrieWarmer)
        {
            if (address is null)
            {
                _loadRlpReadTrieWarmer.Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _loadStorageRlpReadTrieWarmer.Observe(Stopwatch.GetTimestamp() - sw);
            }
        }
        else
        {
            if (address is null)
            {
                _loadRlpRead.Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _loadStorageRlpRead.Observe(Stopwatch.GetTimestamp() - sw);
            }
        }
        return res;
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

    public bool ShouldPrewarm(Address address, UInt256? slot)
    {
        return _wasPrewarmed.TryAdd((address, slot), true);
    }

    public void MaybePreReadAccount(Address address, int sequenceId)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        if (_changedAccounts.ContainsKey(address)) return;

        if (TryGetAccount(address, out Account? account))
        {
            if (_hintLock.TryEnterReadLock(0))
            {
                try
                {
                    if (_hintSequenceId != sequenceId) return;

                    // Note: self destruct and change account is not atomic together obviously,
                    // but the write batch cannot run with this because of the hintlock.
                    // So self destruct should be correct here.
                    if (_selfDestructedAccountAddresses.ContainsKey(address)) return;
                    _changedAccounts.TryAdd(address, account);
                }
                finally
                {
                    _hintLock.ExitReadLock();
                }
            }
        }
    }

    public void MaybePreReadSlot(Address address, UInt256 slot, int sequenceId)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        if (_changedSlots.ContainsKey((address, slot))) return;

        if (TryGetSlot(address, slot, DetermineSelfDestructStateIdx(address), out byte[]? value))
        {
            if (_hintLock.TryEnterReadLock(0))
            {
                try
                {
                    if (_hintSequenceId != sequenceId) return;

                    if(_selfDestructedAccountAddresses.ContainsKey(address)) return;
                    _changedSlots.TryAdd((address, slot), value);
                }
                finally
                {
                    _hintLock.ExitReadLock();
                }
            }
        }
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
        _wasPrewarmed.Clear();

        if (!_isReadOnly) _resourcePool.ReturnSnapshotContent(_currentPooledContent);
    }

    public void Clear(Address address, Hash256AsKey addressHash)
    {
        if (_isReadOnly) throw new InvalidOperationException("Read only snapshot bundle");
        bool isNewAccount = false;
        if (TryGetAccount(address, out Account? account))
        {
            // So... a clear is always sent even on new account. This makes is a minor optimization as
            // it skip persistence, but probably need to make sure it does not send it at all in the first place.
            isNewAccount = account == null;
        }
        _selfDestructedAccountAddresses.TryAdd(address, isNewAccount);

        if (!isNewAccount)
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
        }
    }
}
