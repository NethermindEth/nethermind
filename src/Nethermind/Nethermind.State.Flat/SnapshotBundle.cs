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
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat;

/// <summary>
/// A bundle of <see cref="Snapshot"/> and a layer of write buffer backed by a <see cref="SnapshotContent"/>.
/// </summary>
public class SnapshotBundle : ISnapshotBundleTrieProvider, IDisposable
{
    private ReadOnlySnapshotBundle _readOnlySnapshotBundle;


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

    public int SnapshotCount => _snapshots.Count;

    internal ArrayPoolList<Snapshot> _snapshots;
    private readonly TrieNodeCache _trieNodeCache;
    private bool _isPrewarmer;
    private bool _isDisposed;
    private readonly ResourcePool _resourcePool;

    private static Gauge _activeSnapshotBundle = DevMetric.Factory.CreateGauge("snapshot_bundle_active", "event");
    private static Counter _snapshotBundleEvents = DevMetric.Factory.CreateCounter("snapshot_bundle_evens", "event", "type", "is_prewarmer");
    private Counter.Child _nodeGetChanged = null!;
    private Counter.Child _nodeGetSnapshots = null!;
    private Counter.Child _nodeGetTrieCache = null!;
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
    private Histogram.Child _snapshotAccountHit = null!;
    private Histogram.Child _snapshotAccountMiss = null!;
    private Histogram.Child _snapshotStorageHit = null!;
    private Histogram.Child _snapshotStorageMiss = null!;
    private Histogram.Child _snapshotStorageSelfDestructIdx = null!;
    private Histogram.Child _findStorageNodeNodeCacheMiss = null!;

    public SnapshotBundle(
        ReadOnlySnapshotBundle readOnlySnapshotBundle,
        ArrayPoolList<Snapshot> snapshots,
        TrieNodeCache trieNodeCache,
        ResourcePool resourcePool,
        IFlatDiffRepository.SnapshotBundleUsage usage,
        bool isPrewarmer = false)
    {
        _readOnlySnapshotBundle = readOnlySnapshotBundle;
        _snapshots = snapshots;
        _trieNodeCache = trieNodeCache;
        _resourcePool = resourcePool;
        _isPrewarmer = isPrewarmer;
        _usage = usage;
        SetupMetric();

        _currentPooledContent = resourcePool.GetSnapshotContent(usage);
        _cachedResource = resourcePool.GetCachedResource(usage);

        ExpandCurrentPooledContent();

        _activeSnapshotBundle.Inc();
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
        _accountGet = _snapshotBundleEvents.WithLabels("account_get", _isPrewarmer.ToString());
        _slotGet = _snapshotBundleEvents.WithLabels("slot_get", _isPrewarmer.ToString());

        _findStateNode = _snapshotBundleTimes.WithLabels("find_state_node", _isPrewarmer.ToString());

        _snapshotAccountHit = _snapshotBundleTimes.WithLabels("snapshot_account_hit", _isPrewarmer.ToString());
        _snapshotAccountMiss = _snapshotBundleTimes.WithLabels("snapshot_account_miss", _isPrewarmer.ToString());
        _snapshotStorageHit = _snapshotBundleTimes.WithLabels("snapshot_storage_hit", _isPrewarmer.ToString());
        _snapshotStorageMiss = _snapshotBundleTimes.WithLabels("snapshot_storage_miss", _isPrewarmer.ToString());
        _snapshotStorageSelfDestructIdx = _snapshotBundleTimes.WithLabels("snapshot_storage_selfdestruct_idx", _isPrewarmer.ToString());

        _findStorageNodeLoadedNodes = _snapshotBundleTimes.WithLabels("find_storage_node_loaded_nodes", _isPrewarmer.ToString());
        _findStorageNodeChangedNodes = _snapshotBundleTimes.WithLabels("find_storage_node_changed_nodes", _isPrewarmer.ToString());;
        _findStorageNodeSnapshots = _snapshotBundleTimes.WithLabels("find_storage_node_snapshots", _isPrewarmer.ToString());;
        _findStorageNodeNodeCache = _snapshotBundleTimes.WithLabels("find_storage_node_node_cache", _isPrewarmer.ToString());;
        _findStorageNodeNodeCacheMiss = _snapshotBundleTimes.WithLabels("find_storage_node_node_cache_miss", _isPrewarmer.ToString());;

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
        GuardDispose();

        _accountGet.Inc();

        if (!excludeChanged && _changedAccounts.TryGetValue(address, out acc)) return true;

        AddressAsKey key = address;

        long sw = Stopwatch.GetTimestamp();
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetAccount(key, out acc))
            {
                _snapshotAccountHit.Observe(Stopwatch.GetTimestamp() - sw);
                return true;
            }
        }
        _snapshotAccountMiss.Observe(Stopwatch.GetTimestamp() - sw);

        return _readOnlySnapshotBundle.TryGetAccount(address, out acc);
    }

    public int DetermineSelfDestructSnapshotIdx(Address address)
    {
        // Node: need to add the _readOnlySnapshotBundle.SnapshotCount to get the actual index
        if (_selfDestructedAccountAddresses.ContainsKey(address))
        {
            return _snapshots.Count + _readOnlySnapshotBundle.SnapshotCount;
        }

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].HasSelfDestruct(address))
            {
                return i + _readOnlySnapshotBundle.SnapshotCount;
            }
        }

        return _readOnlySnapshotBundle.DetermineSelfDestructSnapshotIdx(address);
    }

    public bool TryGetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out byte[]? value)
    {
        GuardDispose();

        _slotGet.Inc();

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

        // Self destructed at the point of latest change
        if (selfDestructStateIdx == _snapshots.Count + _readOnlySnapshotBundle.SnapshotCount)
        {
            _nodeGetSelfDestruct.Inc();
            value = null;
            return true;
        }

        int currentBundleSelfDestructIdx = selfDestructStateIdx - _readOnlySnapshotBundle.SnapshotCount;
        if (selfDestructStateIdx == -1 || currentBundleSelfDestructIdx >= 0)
        {
            long sw = Stopwatch.GetTimestamp();
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (_snapshots[i].TryGetStorage(address, index, out slotValue))
                {
                    _snapshotStorageHit.Observe(Stopwatch.GetTimestamp() - sw);
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

                if (i <= currentBundleSelfDestructIdx)
                {
                    // This is the snapshot with selfdestruct
                    _snapshotStorageSelfDestructIdx.Observe(Stopwatch.GetTimestamp() - sw);
                    value = null;
                    _snapshotStorageMiss.Observe(Stopwatch.GetTimestamp() - sw);
                    return true;
                }
            }
        }

        return _readOnlySnapshotBundle.TryGetSlot(address, index, selfDestructStateIdx, out value);
    }

    public void SetChangedSlot(AddressAsKey address, in UInt256 index, byte[] value)
    {
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
        GuardDispose();

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

        return _readOnlySnapshotBundle.TryFindStateNodes(path, hash, out node);
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash, int selfDestructKnownStateIdx, bool isTrieWarmer)
    {
        return FindStorageNodeOrUnknown(address, path, hash, selfDestructKnownStateIdx, isTrieWarmer, storageInitializer: false);
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash, int selfDestructStateIdx, bool isTrieWarmer, bool storageInitializer = false)
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
        GuardDispose();

        TrieNode? node;
        long sw = Stopwatch.GetTimestamp();

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
                _findStorageNodeNodeCache.Observe(Stopwatch.GetTimestamp() - sw);
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                _nodeGetTrieCache.Inc();
                return true;
            }
            _findStorageNodeNodeCacheMiss.Observe(Stopwatch.GetTimestamp() - sw);
        }

        int currentBundleSelfDestructIdx = selfDestructStateIdx - _readOnlySnapshotBundle.SnapshotCount;
        if (selfDestructStateIdx == -1 || currentBundleSelfDestructIdx >= 0)
        {
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (_snapshots[i].TryGetStorageNode(address, path, out node))
                {
                    if (!isTrieWarmer) _findStorageNodeSnapshots.Observe(Stopwatch.GetTimestamp() - sw);
                    Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                    _nodeGetSnapshots.Inc();
                    return true;
                }

                if (i >= currentBundleSelfDestructIdx)
                {
                    if (!isTrieWarmer) _findStorageNodeSnapshots.Observe(Stopwatch.GetTimestamp() - sw);
                    Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                    node = null;
                    return true;
                }
            }
        }

        return _readOnlySnapshotBundle.TryFindStorageNodes(address, path, hash, selfDestructStateIdx, out node);
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags, bool isTrieWarmer)
    {
        GuardDispose();

        return _readOnlySnapshotBundle.TryLoadRlp(address, path, hash, flags, isTrieWarmer);
    }

    // This is called only during trie commit
    public void SetStateNode(in TreePath path, TrieNode newNode)
    {
        GuardDispose();
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        long sw = Stopwatch.GetTimestamp();
        // Note: Hot path
        _changedStateNodes[path] = newNode;
        _setStateNodesTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

    // This is called only during trie commit
    public void SetStorageNode(Hash256 addr, in TreePath path, TrieNode newNode)
    {
        GuardDispose();
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
        // When assembling the snapshot, we straight up pass the _currentPooledContent into the new snapshot
        // This is because copying the values have a measurable impact on overall performance.
        var snapshot = new Snapshot(
            from: from,
            to: to,
            content: _currentPooledContent,
            resourcePool: _resourcePool,
            usage: _usage);

        snapshot.AcquireLease(); // For this SnapshotBundle.
        _snapshots.Add(snapshot); // Now later reads are correct

        // Invalidate cached resources
        if (returnSnapshot)
        {
            CachedResource cachedResource = _cachedResource;

            if (_usage == IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing)
            {
                _usage = IFlatDiffRepository.SnapshotBundleUsage.PostMainBlockProcessing;
            }

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

            _cachedResource.Reset();
            _currentPooledContent = _resourcePool.GetSnapshotContent(_usage);

            return (null, null);
        }
    }

    private void GuardDispose()
    {
        if (_isDisposed) throw new ObjectDisposedException($"{nameof(SnapshotBundle)} disposed");
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

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

        _resourcePool.ReturnSnapshotContent(_usage, _currentPooledContent);
        _resourcePool.ReturnCachedResource(_usage, _cachedResource);
        _readOnlySnapshotBundle.Dispose();

        _activeSnapshotBundle.Dec();
    }

    // Also called SelfDestruct
    public void Clear(Address address, Hash256AsKey addressHash)
    {
        GuardDispose();

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
