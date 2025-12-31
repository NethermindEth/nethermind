// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat;

/// <summary>
/// A bundle of <see cref="Snapshot"/> and a layer of write buffer backed by a <see cref="SnapshotContent"/>.
/// </summary>
public class SnapshotBundle : IDisposable
{
    private SnapshotContent _currentPooledContent = null!;
    // These maps are direct reference from members in _currentPooledContent.
    private ConcurrentDictionary<AddressAsKey, Account?> _changedAccounts = null!;
    private ConcurrentDictionary<TreePath, TrieNode> _changedStateNodes = null!; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> _changedStorageNodes = null!; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<(AddressAsKey, UInt256), SlotValue?> _changedSlots = null!; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<AddressAsKey, bool> _selfDestructedAccountAddresses = null!;

    // The cached resource holds some items that is pooled.
    // Notably it holds loaded caches from trie warmer.
    private CachedResource _cachedResource = null!;

    private readonly bool _forStateReader;

    public int SnapshotCount => _snapshots.Count;

    internal ArrayPoolList<Snapshot> _snapshots;
    private readonly IPersistence.IPersistenceReader _persistenceReader;
    private readonly TrieNodeCache _trieNodeCache;
    private bool _isPrewarmer;
    private bool _isDisposed;
    private readonly ResourcePool _resourcePool;

    private static Gauge _activeSnapshotBundle = DevMetric.Factory.CreateGauge("snapshot_bundle_active", "active", "usage");
    private static Counter _creeatedSnapshotBundle = DevMetric.Factory.CreateCounter("snapshot_bundle_created", "created", "usage");
    private static Counter _snapshotBundleEvents = DevMetric.Factory.CreateCounter("snapshot_bundle_evens", "event", "type", "is_prewarmer");
    private Counter.Child _nodeGetChanged = null!;
    private Counter.Child _nodeGetSnapshots = null!;
    private Counter.Child _nodeGetTrieCache = null!;
    private Counter.Child _nodeGetMiss = null!;
    private Counter.Child _nodeGetSelfDestruct = null!;

    private static Histogram _snapshotBundleResultSize = DevMetric.Factory.CreateHistogram("snapshot_bundle_result_size", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "type" },
        Buckets = Histogram.PowersOfTenDividedBuckets(1, 9, 10)
    });

    private static Histogram _snapshotBundleTimes = DevMetric.Factory.CreateHistogram("snapshot_bundle_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "type", "is_prewarmer" },
        Buckets = Histogram.PowersOfTenDividedBuckets(1, 12, 5)
    });
    private Histogram.Child _accountPersistenceRead = null!;
    private Histogram.Child _slotPersistenceRead = null!;
    private Histogram.Child _accountPersistenceEmptyRead = null!;
    private Histogram.Child _slotPersistenceEmptyRead = null!;
    private Histogram.Child _loadRlpRead = null!;
    private Histogram.Child _loadRlpReadTrieWarmer = null!;
    private Histogram.Child _loadStorageRlpRead = null!;
    private Histogram.Child _loadStorageRlpReadTrieWarmer = null!;
    private Histogram.Child _findStateNode = null!;
    private Histogram.Child _findStorageNodeLoadedNodes = null!;
    private Histogram.Child _findStorageNodeChangedNodes = null!;
    private Histogram.Child _findStorageNodeSnapshots = null!;
    private Histogram.Child _findStorageNodeNodeCache = null!;
    private Histogram.Child _findStateNodeTrieWarmer = null!;
    private Histogram.Child _findStorageNode = null!;
    private Histogram.Child _findStorageNodeInitializer = null!;
    private Histogram.Child _findStorageNodeTrieWarmer = null!;
    private Histogram.Child _setStateNodesTime = null!;
    private Histogram.Child _setStorageNodesTime = null!;
    private Histogram.Child _setSlotTime = null!;
    private Histogram.Child _setSlotToZeroTime = null!;
    private Histogram.Child _setAccountTime = null!;
    private Histogram.Child _shouldPrewarmHit = null!;
    private Histogram.Child _shouldPrewarmMiss = null!;

    private Counter.Child _accountGet = null!;
    private Counter.Child _slotGet = null!;

    internal IFlatDiffRepository.SnapshotBundleUsage _usage;

    public SnapshotBundle(ArrayPoolList<Snapshot> snapshots,
        IPersistence.IPersistenceReader persistenceReader,
        TrieNodeCache trieNodeCache,
        ResourcePool resourcePool,
        IFlatDiffRepository.SnapshotBundleUsage usage,
        bool isPrewarmer = false)
    {
        _snapshots = snapshots;
        _persistenceReader = persistenceReader;
        _trieNodeCache = trieNodeCache;
        _resourcePool = resourcePool;
        _isPrewarmer = isPrewarmer;
        _forStateReader = usage == IFlatDiffRepository.SnapshotBundleUsage.StateReader;
        _usage = usage;
        _activeSnapshotBundle.WithLabels(_usage.ToString()).Inc();
        _creeatedSnapshotBundle.WithLabels(_usage.ToString()).Inc();

        SetupMetric();

        if (!_forStateReader)
        {
            _currentPooledContent = resourcePool.GetSnapshotContent(usage);
            _cachedResource = resourcePool.GetCachedResource(usage);

            ExpandCurrentPooledContent();
        }
    }

    private void ExpandCurrentPooledContent()
    {
        _changedAccounts = _currentPooledContent.Accounts;
        _changedSlots = _currentPooledContent.Storages;
        _changedStorageNodes = _currentPooledContent.StorageNodes;
        _changedStateNodes = _currentPooledContent.StateNodes;
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
        _findStateNode = _snapshotBundleTimes.WithLabels("find_state_node", _isPrewarmer.ToString());

        _findStorageNodeLoadedNodes = _snapshotBundleTimes.WithLabels("find_storage_node_loaded_nodes", _isPrewarmer.ToString());
        _findStorageNodeChangedNodes = _snapshotBundleTimes.WithLabels("find_storage_node_changed_nodes", _isPrewarmer.ToString());;
        _findStorageNodeSnapshots = _snapshotBundleTimes.WithLabels("find_storage_node_snapshots", _isPrewarmer.ToString());;
        _findStorageNodeNodeCache = _snapshotBundleTimes.WithLabels("find_storage_node_node_cache", _isPrewarmer.ToString());;

        _findStateNodeTrieWarmer = _snapshotBundleTimes.WithLabels("find_state_node_trie_warmer", _isPrewarmer.ToString());
        _findStorageNode = _snapshotBundleTimes.WithLabels("find_storage_node", _isPrewarmer.ToString());
        _findStorageNodeInitializer = _snapshotBundleTimes.WithLabels("find_storage_node_initializer", _isPrewarmer.ToString());
        _findStorageNodeTrieWarmer = _snapshotBundleTimes.WithLabels("find_storage_node_trie_warmer", _isPrewarmer.ToString());
        _setSlotTime = _snapshotBundleTimes.WithLabels("set_slot", _isPrewarmer.ToString());
        _setSlotToZeroTime = _snapshotBundleTimes.WithLabels("set_slot_zero", _isPrewarmer.ToString());
        _setAccountTime = _snapshotBundleTimes.WithLabels("set_account", _isPrewarmer.ToString());
        _shouldPrewarmHit = _snapshotBundleTimes.WithLabels("should_prewarm_hit", _isPrewarmer.ToString());
        _shouldPrewarmMiss = _snapshotBundleTimes.WithLabels("should_prewarm_miss", _isPrewarmer.ToString());

        _setStateNodesTime = _snapshotBundleTimes.WithLabels("set_state_nodes", _isPrewarmer.ToString());
        _setStorageNodesTime = _snapshotBundleTimes.WithLabels("set_storage_nodes", _isPrewarmer.ToString());
    }

    public bool TryGetAccount(Address address, out Account? acc)
    {
        return DoTryGetAccount(address, false, out acc);
    }

    private bool DoTryGetAccount(Address address, bool excludeChanged, out Account? acc)
    {
        if (_isDisposed)
        {
            acc = null;
            return false;
        }

        _accountGet.Inc();
        if (!_forStateReader && !excludeChanged)
        {
            if (_changedAccounts.TryGetValue(address, out acc)) return true;
        }

        AddressAsKey key = address;

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetAccount(key, out acc))
            {
                return true;
            }
        }

        long sw = Stopwatch.GetTimestamp();
        acc = _persistenceReader.GetAccount(address);
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

    public int DetermineSelfDestructSnapshotIdx(Address address)
    {
        if (_selfDestructedAccountAddresses.ContainsKey(address))
        {
            return _snapshots.Count;
        }

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].HasSelfDestruct(address))
            {
                return i;
            }
        }

        return -1;
    }

    public bool TryGetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out byte[]? value)
    {
        if (_isDisposed)
        {
            value = null;
            return false;
        }

        _slotGet.Inc();

        if (!_forStateReader)
        {
            if (_changedSlots.TryGetValue((address, index), out SlotValue? slotValue))
            {
                if (slotValue is null)
                {
                    value = null;
                }
                else
                {
                    value = slotValue.Value.ToEvmBytes();
                }
                return true;
            }
        }

        if (selfDestructStateIdx == _snapshots.Count)
        {
            _nodeGetSelfDestruct.Inc();
            value = null;
            return true;
        }

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStorage(address, index, out SlotValue? slotValue))
            {
                if (slotValue is null)
                {
                    value = null;
                }
                else
                {
                    value = slotValue.Value.ToEvmBytes();
                }
                return true;
            }

            if (i <= selfDestructStateIdx)
            {
                value = null;
                return true;
            }
        }

        long sw = Stopwatch.GetTimestamp();

        SlotValue outSlotValue = new SlotValue();

        bool _ = _persistenceReader.TryGetSlot(address, index, ref outSlotValue);
        value = outSlotValue.ToEvmBytes();

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

    public void SetChangedSlot(AddressAsKey address, in UInt256 index, byte[] value)
    {
        if (_forStateReader) throw new InvalidOperationException("Read only snapshot bundle");
        // Note: Hot path

        // So right now, if the value is zero, then it is a deletion. This is not the case with verkle where you
        // can set a value to be zero. Because of this distinction, the zerobytes logic is handled here instead of
        // lower down.
        long sw = Stopwatch.GetTimestamp();
        if (value is null || Bytes.AreEqual(value, StorageTree.ZeroBytes))
        {
            _changedSlots[(address, index)] = null;
            _setSlotToZeroTime.Observe(Stopwatch.GetTimestamp() - sw);
        }
        else
        {
            _changedSlots[(address, index)] = SlotValue.FromSpanWithoutLeadingZero(value);
            _setSlotTime.Observe(Stopwatch.GetTimestamp() - sw);
        }
    }

    public TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash, bool isTrieWarmer)
    {
        TrieNode? node;
        if (_forStateReader)
        {
            if (DoFindStateNodeExternal(path, hash, out node))
            {
                return node;
            }
            return new TrieNode(NodeType.Unknown, hash);
        }

        long sw = Stopwatch.GetTimestamp();

        if (!isTrieWarmer)
        {
            // _changedStateNodes is really hot, so we dont touch it during prewarmer.
            if (_changedStateNodes.TryGetValue(path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                _nodeGetChanged.Inc();
                return node;
            }
        }

        if (_cachedResource.TryGetStateNode(path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;

            // if (!isTrieWarmer) _changedStateNodes.TryAdd(path, node);
            _nodeGetChanged.Inc();
            return node;
        }

        if (!DoFindStateNodeExternal(path, hash, out node))
        {
            // The map to holds the unknown nodes is different for trie warmer and the main tries. This prevent
            // random invalid block.
            node = _cachedResource.GetOrAddStateNode(path, new TrieNode(NodeType.Unknown, hash));
        }
        else
        {
            _cachedResource.UpdateStateNode(path, node);
        }

        if (isTrieWarmer)
        {
            _findStateNodeTrieWarmer.Observe(Stopwatch.GetTimestamp() - sw);
        }
        else
        {
            _findStateNode.Observe(Stopwatch.GetTimestamp() - sw);
        }

        return node;
    }

    private bool DoFindStateNodeExternal(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        if (_isDisposed)
        {
            node = null;
            return false;
        }

        if (_trieNodeCache.TryGet(null, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            _nodeGetTrieCache.Inc();
            return true;
        }

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStateNode(path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                _nodeGetSnapshots.Inc();
                return true;
            }
        }

        _nodeGetMiss.Inc();
        return false;
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256AsKey address, in TreePath path, Hash256 hash,
        int selfDestructStateIdx, bool isTrieWarmer, bool storageInitializer = false)
    {
        long sw = Stopwatch.GetTimestamp();
        var res = DoFindStorageNodeOrUnknown(address, path, hash, selfDestructStateIdx, isTrieWarmer);

        if (isTrieWarmer)
        {
            _findStorageNodeTrieWarmer.Observe(Stopwatch.GetTimestamp() - sw);
        }
        else
        {
            if (storageInitializer)
            {
                _findStorageNodeInitializer.Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _findStorageNode.Observe(Stopwatch.GetTimestamp() - sw);
            }
        }

        return res;
    }

    private TrieNode DoFindStorageNodeOrUnknown(Hash256AsKey address, in TreePath path, Hash256 hash, int selfDestructStateIdx, bool isTrieWarmer)
    {
        if (_isDisposed)
        {
            return new TrieNode(NodeType.Unknown, hash);
        }

        TrieNode? node;
        long sw = Stopwatch.GetTimestamp();

        if (_forStateReader)
        {
            if (DoTryFindStorageNodeExternal(address, path, hash, selfDestructStateIdx, isTrieWarmer, sw, out node) && node is not null)
            {
                return node;
            }
            return new TrieNode(NodeType.Unknown, hash);
        }

        if (!isTrieWarmer)
        {
            if (_changedStorageNodes.TryGetValue((address, path), out node))
            {
                if (!isTrieWarmer) _findStorageNodeChangedNodes.Observe(Stopwatch.GetTimestamp() - sw);
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return node;
            }
        }

        if (_cachedResource.TryGetStorageNode(address, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            if (!isTrieWarmer) _findStorageNodeLoadedNodes.Observe(Stopwatch.GetTimestamp() - sw);
            // if (!isTrieWarmer) _changedStorageNodes.TryAdd((address, path), node);
            return node;
        }

        if (!DoTryFindStorageNodeExternal(address, path, hash, selfDestructStateIdx, isTrieWarmer, sw, out node) || node is null)
        {
            node = _cachedResource.GetOrAddStorageNode(address, path, new TrieNode(NodeType.Unknown, hash));
        }
        else
        {
            _cachedResource.UpdateStorageNode(address, path, node);
        }

        return node;
    }

    private bool DoTryFindStorageNodeExternal(Hash256AsKey address, in TreePath path, Hash256 hash, int selfDestructStateIdx, bool isTrieWarmer, long sw, out TrieNode? node)
    {
        if (selfDestructStateIdx == -1)
        {
            // If no self destruct idx, check node cache first
            if (_trieNodeCache.TryGet(address, path, hash, out node))
            {
                if (!isTrieWarmer) _findStorageNodeNodeCache.Observe(Stopwatch.GetTimestamp() - sw);
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                _nodeGetTrieCache.Inc();
                return true;
            }
        }

        for (int i = _snapshots.Count - 1; i >= 0 && i >= selfDestructStateIdx; i--)
        {
            if (_snapshots[i].TryGetStorageNode(address, path, out node))
            {
                if (!isTrieWarmer) _findStorageNodeSnapshots.Observe(Stopwatch.GetTimestamp() - sw);
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                _nodeGetSnapshots.Inc();
                return true;
            }
        }

        if (selfDestructStateIdx != -1)
        {
            // If there is a self destruct, there is no need to check further, return true
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            _nodeGetSelfDestruct.Inc();
            node = null;
            return true;
        }

        _nodeGetMiss.Inc();
        node = null;
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

    // This is called only during trie commit
    public void SetStateNode(in TreePath path, TrieNode newNode)
    {
        if (_forStateReader) throw new InvalidOperationException("Read only snapshot bundle");
        if (_isDisposed) return;
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        long sw = Stopwatch.GetTimestamp();
        // Note: Hot path
        _changedStateNodes[path] = newNode;
        _setStateNodesTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

    // This is called only during trie commit
    public void SetStorageNode(Hash256AsKey addr, in TreePath path, TrieNode newNode)
    {
        if (_forStateReader) throw new InvalidOperationException("Read only snapshot bundle");
        if (_isDisposed) return;
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        long sw = Stopwatch.GetTimestamp();
        // Note: Hot path
        _changedStorageNodes[(addr, path)] = newNode;
        _setStorageNodesTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

    public void SetAccount(AddressAsKey addr, Account? account)
    {
        if (addr == FlatWorldStateScope.DebugAddress)
        {
            Console.Error.WriteLine($"set to {account}");
        }
        long sw = Stopwatch.GetTimestamp();
        _changedAccounts[addr] = account;
        _setAccountTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

    public bool ShouldPrewarm(Address address, UInt256? slot)
    {
        long sw = Stopwatch.GetTimestamp();
        bool res = _cachedResource.ShouldPrewarm(address, slot);
        if (res)
        {
            _shouldPrewarmHit.Observe(Stopwatch.GetTimestamp() - sw);
        }
        else
        {
            _shouldPrewarmMiss.Observe(Stopwatch.GetTimestamp() - sw);
        }
        return res;
    }

    public (Snapshot?, CachedResource?) CollectAndApplySnapshot(StateId from, StateId to, bool returnSnapshot = true)
    {
        if (_forStateReader) throw new InvalidOperationException("Read only snapshot bundle");

        // When assembling the snapshot, we straight up pass the _currentPooledContent into the new snapshot
        // This is because copying the values have a measurable impact on overall performance.
        var snapshot = new Snapshot(
            from: from,
            to: to,
            content: _currentPooledContent,
            pool: _resourcePool.GetSnapshotPool(_usage));

        snapshot.AcquireLease(); // For this SnapshotBundle.
        _snapshots.Add(snapshot); // Now later reads are correct

        // Invalidate cached resources
        if (returnSnapshot)
        {
            CachedResource cachedResource = _cachedResource;
            _cachedResource = _resourcePool.GetCachedResource(_usage);

            // Make and apply new snapshot content.
            _currentPooledContent = _resourcePool.GetSnapshotContent(_usage);
            ExpandCurrentPooledContent();

            _snapshotBundleResultSize.WithLabels("cached_nodes").Observe(cachedResource.CachedNodes);
            _snapshotBundleResultSize.WithLabels("maybe_warmup_dict").Observe(cachedResource.PrewarmedAddresses.Count);
            _snapshotBundleResultSize.WithLabels("account").Observe(snapshot.AccountsCount);
            _snapshotBundleResultSize.WithLabels("storage").Observe(snapshot.StoragesCount);
            _snapshotBundleResultSize.WithLabels("state_node").Observe(snapshot.StateNodesCount);
            _snapshotBundleResultSize.WithLabels("storage_node").Observe(snapshot.StorageNodesCount);

            return (snapshot, cachedResource);
        }
        else
        {
            snapshot.Dispose(); // Revert the lease before

            _cachedResource.Clear();
            _currentPooledContent = _resourcePool.GetSnapshotContent(_usage);

            return (null, null);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        foreach (Snapshot snapshot in _snapshots)
        {
            snapshot.Dispose();
        }

        // Null them in case unexpected mutation from trie warmer
        _snapshots = null!;
        _changedSlots = null!;
        _changedAccounts = null!;
        _changedStorageNodes = null!;
        _selfDestructedAccountAddresses = null!;

        _persistenceReader.Dispose();

        if (!_forStateReader)
        {
            _resourcePool.ReturnSnapshotContent(_usage, _currentPooledContent);
            _resourcePool.ReturnCachedResource(_usage, _cachedResource);
        }

        _activeSnapshotBundle.WithLabels(_usage.ToString()).Dec();
    }

    // Also called SelfDestruct
    public void Clear(Address address, Hash256AsKey addressHash)
    {
        if (_forStateReader) throw new InvalidOperationException("Read only snapshot bundle");
        bool isNewAccount = false;
        if (DoTryGetAccount(address, excludeChanged: true, out Account? account))
        {
            // So... a clear is always sent even on new account. This makes is a minor optimization as
            // it skip persistence, but probably need to make sure it does not send it at all in the first place.
            isNewAccount = account == null;
            if (address == FlatWorldStateScope.DebugAddress)
            {
                Console.Error.WriteLine($"The clear newness is {isNewAccount}");
            }
        }
        else
        {
            if (address == FlatWorldStateScope.DebugAddress)
            {
                Console.Error.WriteLine("The clear is not new");
            }
        }
        _selfDestructedAccountAddresses.TryAdd(address, isNewAccount);

        if (!isNewAccount)
        {
            foreach (var kv in _changedStorageNodes)
            {
                if (kv.Key.Item1.Value == addressHash)
                {
                    _changedStorageNodes.TryRemove(kv.Key, out TrieNode? _);
                }
            }

            foreach (var kv in _changedSlots)
            {
                if (kv.Key.Item1.Value == address)
                {
                    _changedSlots.TryRemove(kv.Key, out _);
                }
            }
        }
    }
}
