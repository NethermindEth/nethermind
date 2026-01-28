// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A bundle of <see cref="Snapshot"/> and a layer of write buffer backed by a <see cref="SnapshotContent"/>.
/// </summary>
public sealed class SnapshotBundle : IDisposable
{
    private readonly ReadOnlySnapshotBundle _readOnlySnapshotBundle;


    private SnapshotContent _currentPooledContent = null!;
    // These maps are direct reference from members in _currentPooledContent.
    private ConcurrentDictionary<AddressAsKey, Account?> _changedAccounts = null!;
    private ConcurrentDictionary<TreePath, TrieNode> _changedStateNodes = null!; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> _changedStorageNodes = null!; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<(AddressAsKey, UInt256), SlotValue?> _changedSlots = null!; // Bulkset can get nodes concurrently
    private ConcurrentDictionary<AddressAsKey, bool> _selfDestructedAccountAddresses = null!;

    // The cached resource holds some items that are pooled.
    // Notably, it holds loaded caches from trie warmer.
    private TransientResource _transientResource = null!;

    internal SnapshotPooledList _snapshots;
    private readonly ITrieNodeCache _trieNodeCache;
    private bool _isDisposed;
    private readonly IResourcePool _resourcePool;

    internal ResourcePool.Usage _usage;

    public SnapshotBundle(
        ReadOnlySnapshotBundle readOnlySnapshotBundle,
        ITrieNodeCache trieNodeCache,
        IResourcePool resourcePool,
        ResourcePool.Usage usage,
        SnapshotPooledList? snapshots = null)
    {
        _readOnlySnapshotBundle = readOnlySnapshotBundle;
        _snapshots = snapshots ?? new SnapshotPooledList(1);
        _trieNodeCache = trieNodeCache;
        _resourcePool = resourcePool;
        _usage = usage;

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

    public Account? GetAccount(Address address) => DoGetAccount(address, false);

    private Account? DoGetAccount(Address address, bool excludeChanged)
    {
        GuardDispose();

        if (!excludeChanged && _changedAccounts.TryGetValue(address, out Account? acc)) return acc;

        AddressAsKey key = address;
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetAccount(key, out acc))
            {
                return acc;
            }
        }

        return _readOnlySnapshotBundle.GetAccount(address);
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

    public byte[]? GetSlot(Address address, in UInt256 index, int selfDestructStateIdx)
    {
        GuardDispose();

        if (_changedSlots.TryGetValue((address, index), out SlotValue? slotValue))
        {
            return slotValue?.ToEvmBytes();
        }

        // Self-destructed at the point of the latest change
        if (selfDestructStateIdx == _snapshots.Count + _readOnlySnapshotBundle.SnapshotCount)
        {
            return null;
        }

        int currentBundleSelfDestructIdx = selfDestructStateIdx - _readOnlySnapshotBundle.SnapshotCount;
        if (selfDestructStateIdx == -1 || currentBundleSelfDestructIdx >= 0)
        {
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (_snapshots[i].TryGetStorage(address, index, out slotValue))
                {
                    return slotValue?.ToEvmBytes();
                }

                if (i <= currentBundleSelfDestructIdx)
                {
                    // This is the snapshot with selfdestruct
                    return null;
                }
            }
        }

        return _readOnlySnapshotBundle.GetSlot(address, index, selfDestructStateIdx);
    }

    public TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash)
    {
        GuardDispose();

        if (_changedStateNodes.TryGetValue(path, out TrieNode? node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
        }
        else if (_transientResource.TryGetStateNode(path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            node = _changedStateNodes.GetOrAdd(path, node);
        }
        else
        {
            node = _changedStateNodes.GetOrAdd(path,
                DoFindStateNodeExternal(path, hash, out node)
                    ? node
                    : new TrieNode(NodeType.Unknown, hash));
        }

        return node;
    }

    public TrieNode FindStateNodeOrUnknownForTrieWarmer(in TreePath path, Hash256 hash)
    {
        // TrieWarmer only touch `_transientResource`
        GuardDispose();

        if (_transientResource.TryGetStateNode(path, hash, out TrieNode? node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
        }
        else
        {
            node = _transientResource.GetOrAddStateNode(path,
                DoFindStateNodeExternal(path, hash, out node)
                    ? node
                    : new TrieNode(NodeType.Unknown, hash));
        }

        return node;
    }

    private bool DoFindStateNodeExternal(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        if (_trieNodeCache.TryGet(null, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return true;
        }

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStateNode(path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return true;
            }
        }

        return _readOnlySnapshotBundle.TryFindStateNodes(path, hash, out node);
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash)
    {
        GuardDispose();

        if (_changedStorageNodes.TryGetValue(((Hash256AsKey)address, path), out TrieNode? node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            _transientResource.UpdateStorageNode((Hash256AsKey)address, path, node);
        }
        else if (_transientResource.TryGetStorageNode((Hash256AsKey)address, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            node = _changedStorageNodes.GetOrAdd(((Hash256AsKey)address, path), node);
        }
        else
        {
            node = _changedStorageNodes.GetOrAdd(((Hash256AsKey)address, path),
                DoTryFindStorageNodeExternal((Hash256AsKey)address, path, hash, out node) && node is not null
                    ? node
                    : new TrieNode(NodeType.Unknown, hash));
        }

        return node;
    }


    public TrieNode FindStorageNodeOrUnknownTrieWarmer(Hash256 address, in TreePath path, Hash256 hash)
    {
        GuardDispose();

        if (_transientResource.TryGetStorageNode((Hash256AsKey)address, path, hash, out TrieNode? node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
        }
        else
        {
            node = _transientResource.GetOrAddStorageNode((Hash256AsKey)address, path,
                DoTryFindStorageNodeExternal((Hash256AsKey)address, path, hash, out node) && node is not null
                    ? node
                    : new TrieNode(NodeType.Unknown, hash));
        }

        return node;
    }

    // Note: No self-destruct boundary check needed for trie nodes. Trie iteration starts from the storage root hash,
    // so if storage was self-destructed, the new root is different and orphaned nodes won't be traversed. So we skip the
    // check for slightly improved latency.
    private bool DoTryFindStorageNodeExternal(Hash256AsKey address, in TreePath path, Hash256 hash, out TrieNode? node)
    {
        if (_trieNodeCache.TryGet(address, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return true;
        }

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStorageNode(address, path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return true;
            }
        }

        return _readOnlySnapshotBundle.TryFindStorageNodes(address, path, hash, out node);
    }

    public byte[]? TryLoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        return _readOnlySnapshotBundle.TryLoadStateRlp(path, hash, flags);
    }

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        return _readOnlySnapshotBundle.TryLoadStorageRlp(address, path, hash, flags);
    }

    // This is called only during trie commit
    public void SetStateNode(in TreePath path, TrieNode newNode)
    {
        GuardDispose();
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        // Note: Hot path
        _changedStateNodes[path] = newNode;

        // Note to self:
        // Skipping the cached resource update and doing it in background in TrieNodeCache barely make a dent
        // to block processing time but increase the trie node add time by 3x.
        _transientResource.UpdateStateNode(path, newNode);
    }

    // This is called only during trie commit
    public void SetStorageNode(Hash256 addr, in TreePath path, TrieNode newNode)
    {
        GuardDispose();
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        // Note: Hot path
        _changedStorageNodes[(addr, path)] = newNode;
        _transientResource.UpdateStorageNode(addr, path, newNode);
    }

    public void SetAccount(AddressAsKey addr, Account? account) => _changedAccounts[addr] = account;

    public void SetChangedSlot(AddressAsKey address, in UInt256 index, byte[] value)
    {
        // So right now, if the value is zero, then it is a deletion. This is not the case with verkle where you
        // can set a value to be zero. Because of this distinction, the zerobytes logic is handled here instead of
        // lower down.
        if (value is null || Bytes.AreEqual(value, StorageTree.ZeroBytes))
        {
            _changedSlots[(address, index)] = null;
        }
        else
        {
            _changedSlots[(address, index)] = SlotValue.FromSpanWithoutLeadingZero(value);
        }
    }

    // Also called SelfDestruct
    public void Clear(Address address, Hash256AsKey addressHash)
    {
        GuardDispose();

        Account? account = DoGetAccount(address, excludeChanged: true);
        // So... a clear is always sent even on a new account. This makes is a minor optimization as
        // it skips persistence, but probably need to make sure it does not send it at all in the first place.
        bool isNewAccount = account == null || account.StorageRoot == Keccak.EmptyTreeHash;

        _selfDestructedAccountAddresses.TryAdd(address, isNewAccount);

        if (!isNewAccount)
        {
            // Collect keys first to avoid modifying during iteration
            using ArrayPoolListRef<(Hash256AsKey, TreePath)> storageKeysToRemove = new(0);
            foreach (KeyValuePair<(Hash256AsKey, TreePath), TrieNode> kv in _changedStorageNodes)
            {
                if (kv.Key.Item1.Value == addressHash)
                {
                    storageKeysToRemove.Add(kv.Key);
                }
            }

            foreach ((Hash256AsKey, TreePath) key in storageKeysToRemove)
            {
                _changedStorageNodes.TryRemove(key, out _);
            }

            ArrayPoolListRef<(AddressAsKey, UInt256)> slotKeysToRemove = new(0);
            foreach (KeyValuePair<(AddressAsKey, UInt256), SlotValue?> kv in _changedSlots)
            {
                if (kv.Key.Item1.Value == address)
                {
                    slotKeysToRemove.Add(kv.Key);
                }
            }

            foreach ((AddressAsKey, UInt256) key in slotKeysToRemove)
            {
                _changedSlots.TryRemove(key, out _);
            }
        }
    }

    // The trie warmer's PushSlotJob is slightly slow due to the wake up logic.
    // It is a net improvement to check and modify the bloom filter before calling the trie warmer push
    // as most of the slot should already be queued by prewarmer.
    public bool ShouldQueuePrewarm(Address address, UInt256? slot = null) => _transientResource.ShouldPrewarm(address, slot);

    public (Snapshot?, TransientResource?) CollectAndApplySnapshot(StateId from, StateId to, bool returnSnapshot = true)
    {
        // When assembling the snapshot, we straight up pass the _currentPooledContent into the new snapshot
        // This is because copying the values have a measurable impact on overall performance.
        Snapshot snapshot = new(
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

            // Main block processing only commits once. For optimization, we switch the usage so that the used resource
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

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);

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
