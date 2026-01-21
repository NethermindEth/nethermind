// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Metric;
using Nethermind.Int256;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A bundle of <see cref="Snapshot"/> and a layer of write buffer backed by a <see cref="SnapshotContent"/>.
/// </summary>
public sealed class SnapshotBundle : IDisposable
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
    private TransientResource _transientResource = null!;

    internal SnapshotPooledList _snapshots;
    private readonly TrieNodeCache _trieNodeCache;
    private bool _isPrewarmer;
    private bool _isDisposed;
    private readonly ResourcePool _resourcePool;

    // Cached label for this instance
    private TwoStringLabel _nodeGetChangedLabel = null!;
    private TwoStringLabel _nodeGetSnapshotsLabel = null!;
    private TwoStringLabel _nodeGetTrieCacheLabel = null!;
    private TwoStringLabel _nodeGetSelfDestructLabel = null!;
    private TwoStringLabel _findStateNodeLabel = null!;
    private TwoStringLabel _findStorageNodeLoadedNodesLabel = null!;
    private TwoStringLabel _findStorageNodeChangedNodesLabel = null!;
    private TwoStringLabel _findStateNodeTrieWarmerLabel = null!;
    private TwoStringLabel _findStorageNodeLabel = null!;
    private TwoStringLabel _findStorageNodeTrieWarmerLabel = null!;
    private TwoStringLabel _setStateNodesTimeLabel = null!;
    private TwoStringLabel _setStorageNodesTimeLabel = null!;
    private TwoStringLabel _setSlotTimeLabel = null!;
    private TwoStringLabel _setSlotToZeroTimeLabel = null!;
    private TwoStringLabel _setAccountTimeLabel = null!;
    private TwoStringLabel _loadStateRlpTrieWarmerLabel = null!;
    private TwoStringLabel _loadStateRlpLabel = null!;
    private TwoStringLabel _loadStorageRlpTrieWarmerLabel = null!;
    private TwoStringLabel _loadStorageRlpLabel = null!;
    private TwoStringLabel _accountGetLabel = null!;
    private TwoStringLabel _slotGetLabel = null!;
    private TwoStringLabel _snapshotAccountHitLabel = null!;
    private TwoStringLabel _snapshotAccountMissLabel = null!;

    internal ResourcePool.Usage _usage;

    public SnapshotBundle(
        ReadOnlySnapshotBundle readOnlySnapshotBundle,
        TrieNodeCache trieNodeCache,
        ResourcePool resourcePool,
        ResourcePool.Usage usage)
    {
        _readOnlySnapshotBundle = readOnlySnapshotBundle;
        _snapshots = new SnapshotPooledList(1);
        _trieNodeCache = trieNodeCache;
        _resourcePool = resourcePool;
        _usage = usage;
        SetupMetric();

        _currentPooledContent = resourcePool.GetSnapshotContent(usage);
        _transientResource = resourcePool.GetCachedResource(usage);

        ExpandCurrentPooledContent();

        Metrics.ActiveSnapshotBundle++;
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
        string isPrewarmerStr = _isPrewarmer.ToString();
        _nodeGetChangedLabel = new TwoStringLabel("node_get_changed", isPrewarmerStr);
        _nodeGetSnapshotsLabel = new TwoStringLabel("node_get_snapshots", isPrewarmerStr);
        _nodeGetTrieCacheLabel = new TwoStringLabel("node_get_trie_cache", isPrewarmerStr);
        _nodeGetSelfDestructLabel = new TwoStringLabel("node_get_self_destruct", isPrewarmerStr);
        _accountGetLabel = new TwoStringLabel("account_get", isPrewarmerStr);
        _slotGetLabel = new TwoStringLabel("slot_get", isPrewarmerStr);

        _findStateNodeLabel = new TwoStringLabel("find_state_node", isPrewarmerStr);

        _snapshotAccountHitLabel = new TwoStringLabel("snapshot_account_hit", isPrewarmerStr);
        _snapshotAccountMissLabel = new TwoStringLabel("snapshot_account_miss", isPrewarmerStr);

        _findStorageNodeLoadedNodesLabel = new TwoStringLabel("find_storage_node_loaded_nodes", isPrewarmerStr);
        _findStorageNodeChangedNodesLabel = new TwoStringLabel("find_storage_node_changed_nodes", isPrewarmerStr);

        _findStateNodeTrieWarmerLabel = new TwoStringLabel("find_state_node_trie_warmer", isPrewarmerStr);
        _findStorageNodeLabel = new TwoStringLabel("find_storage_node", isPrewarmerStr);
        _findStorageNodeTrieWarmerLabel = new TwoStringLabel("find_storage_node_trie_warmer", isPrewarmerStr);
        _setSlotTimeLabel = new TwoStringLabel("set_slot", isPrewarmerStr);
        _setSlotToZeroTimeLabel = new TwoStringLabel("set_slot_zero", isPrewarmerStr);
        _setAccountTimeLabel = new TwoStringLabel("set_account", isPrewarmerStr);

        _setStateNodesTimeLabel = new TwoStringLabel("set_state_nodes", isPrewarmerStr);
        _setStorageNodesTimeLabel = new TwoStringLabel("set_storage_nodes", isPrewarmerStr);

        _loadStateRlpTrieWarmerLabel = new TwoStringLabel("load_state_rlp_trie_warmer", isPrewarmerStr);
        _loadStateRlpLabel = new TwoStringLabel("load_state_rlp", isPrewarmerStr);
        _loadStorageRlpTrieWarmerLabel = new TwoStringLabel("load_storage_rlp_trie_warmer", isPrewarmerStr);
        _loadStorageRlpLabel = new TwoStringLabel("load_storage_rlp", isPrewarmerStr);
    }

    public bool TryGetAccount(Address address, out Account? acc)
    {
        return DoTryGetAccount(address, false, out acc);
    }

    private bool DoTryGetAccount(Address address, bool excludeChanged, out Account? acc)
    {
        GuardDispose();

        Metrics.SnapshotBundleEvents.AddBy(_accountGetLabel, 1);

        if (!excludeChanged && _changedAccounts.TryGetValue(address, out acc)) return true;

        long sw = Stopwatch.GetTimestamp();

        AddressAsKey key = address;
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetAccount(key, out acc))
            {
                Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _snapshotAccountHitLabel);
                return true;
            }
        }
        Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _snapshotAccountMissLabel);

        return _readOnlySnapshotBundle.TryGetAccount(address, out acc);
    }

    public int DetermineSelfDestructSnapshotIdx(Address address)
    {
        if (_selfDestructedAccountAddresses.ContainsKey(address)) return _snapshots.Count + _readOnlySnapshotBundle.SnapshotCount;

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].HasSelfDestruct(address)) return i + _readOnlySnapshotBundle.SnapshotCount;
        }

        return _readOnlySnapshotBundle.DetermineSelfDestructSnapshotIdx(address);
    }

    public bool TryGetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out byte[]? value)
    {
        GuardDispose();

        Metrics.SnapshotBundleEvents.AddBy(_slotGetLabel, 1);

        if (_changedSlots.TryGetValue((address, index), out SlotValue? slotValue))
        {
            value = slotValue is null ? null : slotValue.Value.ToEvmBytes();
            return true;
        }

        // Self destructed at the point of latest change
        if (selfDestructStateIdx == _snapshots.Count + _readOnlySnapshotBundle.SnapshotCount)
        {
            Metrics.SnapshotBundleEvents.AddBy(_nodeGetSelfDestructLabel, 1);
            value = null;
            return true;
        }

        int currentBundleSelfDestructIdx = selfDestructStateIdx - _readOnlySnapshotBundle.SnapshotCount;
        if (selfDestructStateIdx == -1 || currentBundleSelfDestructIdx >= 0)
        {
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (_snapshots[i].TryGetStorage(address, index, out slotValue))
                {
                    value = slotValue is null ? null : slotValue.Value.ToEvmBytes();
                    return true;
                }

                if (i <= currentBundleSelfDestructIdx)
                {
                    // This is the snapshot with selfdestruct
                    value = null;
                    return true;
                }
            }
        }

        return _readOnlySnapshotBundle.TryGetSlot(address, index, selfDestructStateIdx, out value);
    }

    public TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash)
    {
        GuardDispose();

        TrieNode? node;
        long sw = Stopwatch.GetTimestamp();

        if (_changedStateNodes.TryGetValue(path, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            Metrics.SnapshotBundleEvents.AddBy(_nodeGetChangedLabel, 1);
        }
        else if (_transientResource.TryGetStateNode(path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            Metrics.SnapshotBundleEvents.AddBy(_nodeGetChangedLabel, 1);
            node = _changedStateNodes.GetOrAdd(path, node);
        }
        else
        {
            node = _changedStateNodes.GetOrAdd(path,
                DoFindStateNodeExternal(path, hash, out node) ?
                    node :
                    new TrieNode(NodeType.Unknown, hash));
        }

        Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _findStateNodeLabel);

        return node;
    }

    public TrieNode FindStateNodeOrUnknownForTrieWarmer(in TreePath path, Hash256 hash)
    {
        // TrieWarmer only touch `_cachedResource`
        GuardDispose();

        TrieNode? node;
        long sw = Stopwatch.GetTimestamp();

        if (_transientResource.TryGetStateNode(path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            Metrics.SnapshotBundleEvents.AddBy(_nodeGetChangedLabel, 1);
        }
        else
        {
            node = _transientResource.GetOrAddStateNode(path,
                DoFindStateNodeExternal(path, hash, out node)
                    ? node
                    : new TrieNode(NodeType.Unknown, hash));
        }

        Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _findStateNodeTrieWarmerLabel);

        return node;
    }

    private bool DoFindStateNodeExternal(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        if (_trieNodeCache.TryGet(null, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            Metrics.SnapshotBundleEvents.AddBy(_nodeGetTrieCacheLabel, 1);
            return true;
        }

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStateNode(path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                Metrics.SnapshotBundleEvents.AddBy(_nodeGetSnapshotsLabel, 1);
                return true;
            }
        }

        return _readOnlySnapshotBundle.TryFindStateNodes(path, hash, out node);
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash, int selfDestructStateIdx)
    {
        long sw = Stopwatch.GetTimestamp();
        GuardDispose();

        TrieNode? node;
        if (_changedStorageNodes.TryGetValue(((Hash256AsKey)address, path), out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _findStorageNodeChangedNodesLabel);
            _transientResource.UpdateStorageNode((Hash256AsKey)address, path, node);
        }
        else if (_transientResource.TryGetStorageNode((Hash256AsKey)address, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _findStorageNodeLoadedNodesLabel);
            node = _changedStorageNodes.GetOrAdd(((Hash256AsKey)address, path), node);
        }
        else
        {
            node = _changedStorageNodes.GetOrAdd(((Hash256AsKey)address, path),
                (DoTryFindStorageNodeExternal((Hash256AsKey)address, path, hash, selfDestructStateIdx, out node) && node is not null)
                    ? node
                    : new TrieNode(NodeType.Unknown, hash));
        }

        Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _findStorageNodeLabel);

        return node;
    }


    public TrieNode FindStorageNodeOrUnknownTrieWarmer(Hash256 address, in TreePath path, Hash256 hash, int selfDestructStateIdx)
    {
        long sw = Stopwatch.GetTimestamp();
        GuardDispose();

        TrieNode? node;
        if (_transientResource.TryGetStorageNode((Hash256AsKey)address, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
        }
        else
        {
            node = _transientResource.GetOrAddStorageNode((Hash256AsKey)address, path,
                (DoTryFindStorageNodeExternal((Hash256AsKey)address, path, hash, selfDestructStateIdx, out node) && node is not null)
                    ? node
                    : new TrieNode(NodeType.Unknown, hash));
        }

        Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _findStorageNodeTrieWarmerLabel);
        return node;
    }

    private bool DoTryFindStorageNodeExternal(Hash256AsKey address, in TreePath path, Hash256 hash, int selfDestructStateIdx, out TrieNode? node)
    {
        if (_trieNodeCache.TryGet(address, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            Metrics.SnapshotBundleEvents.AddBy(_nodeGetTrieCacheLabel, 1);
            return true;
        }

        int currentBundleSelfDestructIdx = selfDestructStateIdx - _readOnlySnapshotBundle.SnapshotCount;
        if (selfDestructStateIdx == -1 || currentBundleSelfDestructIdx >= 0)
        {
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (_snapshots[i].TryGetStorageNode(address, path, out node))
                {
                    Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                    Metrics.SnapshotBundleEvents.AddBy(_nodeGetSnapshotsLabel, 1);
                    return true;
                }

                if (i >= currentBundleSelfDestructIdx)
                {
                    Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                    node = null;
                    return true;
                }
            }
        }

        return _readOnlySnapshotBundle.TryFindStorageNodes(address, path, hash, selfDestructStateIdx, out node);
    }

    public byte[]? TryLoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags, bool isTrieWarmer)
    {
        GuardDispose();

        long sw = Stopwatch.GetTimestamp();
        byte[]? value = _readOnlySnapshotBundle.TryLoadStateRlp(path, hash, flags);
        if (isTrieWarmer)
            Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _loadStateRlpTrieWarmerLabel);
        else
            Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _loadStateRlpLabel);

        return value;
    }

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, Hash256 hash, ReadFlags flags, bool isTrieWarmer)
    {
        GuardDispose();

        long sw = Stopwatch.GetTimestamp();
        byte[]? value = _readOnlySnapshotBundle.TryLoadStorageRlp(address, path, hash, flags);
        if (isTrieWarmer)
            Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _loadStorageRlpTrieWarmerLabel);
        else
            Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _loadStorageRlpLabel);

        return value;
    }

    // This is called only during trie commit
    public void SetStateNode(in TreePath path, TrieNode newNode)
    {
        GuardDispose();
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        long sw = Stopwatch.GetTimestamp();
        // Note: Hot path
        _changedStateNodes[path] = newNode;

        // Note to self:
        // Skipping the cached resource update and doing it in background in TrieNodeCache barely make a dent
        // to block processing time but increase the trie node add time by 3x.
        _transientResource.UpdateStateNode(path, newNode);
        Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _setStateNodesTimeLabel);
    }

    // This is called only during trie commit
    public void SetStorageNode(Hash256 addr, in TreePath path, TrieNode newNode)
    {
        GuardDispose();
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        long sw = Stopwatch.GetTimestamp();
        // Note: Hot path
        _changedStorageNodes[(addr, path)] = newNode;
        _transientResource.UpdateStorageNode(addr, path, newNode);
        Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _setStorageNodesTimeLabel);
    }

    public void SetAccount(AddressAsKey addr, Account? account)
    {
        long sw = Stopwatch.GetTimestamp();
        _changedAccounts[addr] = account;
        Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _setAccountTimeLabel);
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
            Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _setSlotToZeroTimeLabel);
        }
        else
        {
            _changedSlots[(address, index)] = SlotValue.FromSpanWithoutLeadingZero(value);
            Metrics.SnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _setSlotTimeLabel);
        }
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
            isNewAccount = account == null || account.StorageRoot == Keccak.EmptyTreeHash;
        }

        _selfDestructedAccountAddresses.TryAdd(address, isNewAccount);

        if (!isNewAccount)
        {
            // Collect keys first to avoid modifying during iteration
            ArrayPoolListRef<(Hash256AsKey, TreePath)> storageKeysToRemove = new(0);
            foreach (var kv in _changedStorageNodes)
            {
                if (kv.Key.Item1.Value == addressHash)
                {
                    storageKeysToRemove.Add(kv.Key);
                }
            }

            foreach (var key in storageKeysToRemove)
            {
                _changedStorageNodes.TryRemove(key, out _);
            }

            ArrayPoolListRef<(AddressAsKey, UInt256)> slotKeysToRemove = new(0);
            foreach (var kv in _changedSlots)
            {
                if (kv.Key.Item1.Value == address)
                {
                    slotKeysToRemove.Add(kv.Key);
                }
            }

            foreach (var key in slotKeysToRemove)
            {
                _changedSlots.TryRemove(key, out _);
            }
        }
    }

    public bool ShouldQueuePrewarm(Address address, UInt256? slot)
    {
        // The trie warmer's PushSlotJob is slightly slow due to the wake up logic.
        // It is a net improvement to check and modify the bloom filter before calling the trie warmer push
        // as most of the slot should already be queued by prewarmer.
        return _transientResource.ShouldPrewarm(address, slot);
    }

    public (Snapshot?, TransientResource?) CollectAndApplySnapshot(StateId from, StateId to, bool returnSnapshot = true)
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
            TransientResource transientResource = _transientResource;

            // Main block processing only commit once. For optimization we switch the usage so that the used resource
            // is from a different pool that will essentially be empty all the time.
            if (_usage == ResourcePool.Usage.MainBlockProcessing)
            {
                _usage = ResourcePool.Usage.PostMainBlockProcessing;
            }

            _transientResource = _resourcePool.GetCachedResource(_usage);

            // Make and apply new snapshot content.
            _currentPooledContent = _resourcePool.GetSnapshotContent(_usage);
            ExpandCurrentPooledContent();

            return (snapshot, transientResource);
        }
        else
        {
            snapshot.Dispose(); // Revert the lease before

            _transientResource.Reset();
            _currentPooledContent = _resourcePool.GetSnapshotContent(_usage);

            return (null, null);
        }
    }

    public void Reset()
    {
        if (_isDisposed) return;

        // Dispose all snapshots in the list
        _snapshots.Dispose();
        _snapshots = new SnapshotPooledList(1);

        // Reset the current pooled content (clears _changedAccounts, _changedSlots, etc.)
        _currentPooledContent.Reset();

        // Reset transient resource (clears trie node cache and bloom filter)
        _transientResource.Reset();

        ExpandCurrentPooledContent();
    }

    private void GuardDispose()
    {
        if (_isDisposed) throw new ObjectDisposedException($"{nameof(SnapshotBundle)} disposed");
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        _snapshots.Dispose();

        // Null them in case unexpected mutation from trie warmer
        _snapshots = null!;
        _changedSlots = null!;
        _changedAccounts = null!;
        _changedStorageNodes = null!;
        _selfDestructedAccountAddresses = null!;

        _resourcePool.ReturnSnapshotContent(_usage, _currentPooledContent);
        _resourcePool.ReturnCachedResource(_usage, _transientResource);
        _readOnlySnapshotBundle.Dispose();

        Metrics.ActiveSnapshotBundle--;
    }
}
