// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A mutable bundle wrapping a <see cref="ReadOnlySnapshotBundle"/> with a write buffer backed by <see cref="SnapshotContent"/>.
/// </summary>
public sealed class SnapshotBundle : IDisposable
{
    private readonly ReadOnlySnapshotBundle _readOnlySnapshotBundle;


    private SnapshotContent _currentPooledContent = null!;
    // These maps are direct reference from members in _currentPooledContent.
    private ConcurrentDictionary<AddressAsKey, Account?> _changedAccounts = null!;
    private ConcurrentDictionary<(AddressAsKey, UInt256), SlotValue?> _changedSlots = null!;
    private ConcurrentDictionary<TreePath, RefCountingTrieNode> _changedStateNodes = null!;
    private ConcurrentDictionary<(Hash256AsKey, TreePath), RefCountingTrieNode> _changedStorageNodes = null!;
    private ConcurrentDictionary<AddressAsKey, bool> _selfDestructedAccountAddresses = null!;

    private bool _trieChanged = false;

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
        _transientResource.Nodes.SetShardTrackers(trieNodeCache.ShardTrackers);

        ExpandCurrentPooledContent();

        Metrics.ActiveSnapshotBundle++;
    }

    /// <summary>
    /// Returns the node's RLP as a CappedArray. If the lease pool is available, holds a lease
    /// on the node and returns its Rlp directly (zero copy). Otherwise copies.
    /// </summary>
    private CappedArray<byte> RentRlpOrCopy(RefCountingTrieNode node)
    {
        if (_transientResource.LeasePool.TryHoldLease(node))
            return node.Rlp;
        CappedArray<byte> result = _transientResource.BufferPool.Rent(node.RlpLength);
        node.RlpSpan.CopyTo(result.AsSpan());
        return result;
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

        return _readOnlySnapshotBundle.GetSlot(address, index, selfDestructStateIdx);
    }

    public TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash)
    {
        GuardDispose();

        return new TrieNode(NodeType.Unknown, hash);
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash)
    {
        GuardDispose();

        return new TrieNode(NodeType.Unknown, hash);
    }

    public CappedArray<byte> TryLoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        // Check changed nodes from current block
        if (_trieChanged && _changedStateNodes.TryGetValue(path, out RefCountingTrieNode? changed) && changed.Hash == (ValueHash256)hash && changed.RlpLength > 0)
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return RentRlpOrCopy(changed);
        }

        // Check warmer RLP caches before hitting persistence.
        // Copy to byte[] only here (commit path); warmup path uses RefCountingTrieNode.
        RefCountingTrieNode? cached = _transientResource.TryGetStateNode(path, hash);
        if (cached is not null)
        {
            try
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return RentRlpOrCopy(cached);
            }
            finally { cached.Dispose(); }
        }

        cached = _trieNodeCache.TryGet(null, path, hash);
        if (cached is not null)
        {
            try
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return RentRlpOrCopy(cached);
            }
            finally { cached.Dispose(); }
        }

        // Check local snapshots for nodes committed in prior blocks
        ValueHash256 valueHash = hash;
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStateNode(path, out RefCountingTrieNode? snapshotNode) && snapshotNode.Hash == valueHash && snapshotNode.RlpLength > 0)
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return RentRlpOrCopy(snapshotNode);
            }
        }

        // Check readonly snapshots
        if (_readOnlySnapshotBundle.TryFindStateNodes(path, hash, out cached))
        {
            try
            {
                return RentRlpOrCopy(cached);
            }
            finally { cached.Dispose(); }
        }

        CappedArray<byte> buffer = _transientResource.BufferPool.Rent(RefCountingTrieNode.MaxEthereumBranchRlpLength);
        int len = _readOnlySnapshotBundle.TryLoadStateRlpFromPersistence(path, hash, buffer.AsSpan(), flags);
        if (len > 0) return new CappedArray<byte>(buffer.UnderlyingArray!, len);
        _transientResource.BufferPool.Return(buffer);
        return default;
    }

    public CappedArray<byte> TryLoadStorageRlp(Hash256 address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        // Check changed nodes from current block
        if (_trieChanged && _changedStorageNodes.TryGetValue(((Hash256AsKey)address, path), out RefCountingTrieNode? changed) && changed.Hash == (ValueHash256)hash && changed.RlpLength > 0)
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return RentRlpOrCopy(changed);
        }

        // Check warmer RLP caches before hitting persistence.
        RefCountingTrieNode? cached = _transientResource.TryGetStorageNode((Hash256AsKey)address, path, hash);
        if (cached is not null)
        {
            try
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return RentRlpOrCopy(cached);
            }
            finally { cached.Dispose(); }
        }

        cached = _trieNodeCache.TryGet((Hash256AsKey)address, path, hash);
        if (cached is not null)
        {
            try
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return RentRlpOrCopy(cached);
            }
            finally { cached.Dispose(); }
        }

        // Check local snapshots for nodes committed in prior blocks
        ValueHash256 valueHash = hash;
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStorageNode(address, path, out RefCountingTrieNode? snapshotNode) && snapshotNode.Hash == valueHash && snapshotNode.RlpLength > 0)
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return RentRlpOrCopy(snapshotNode);
            }
        }

        // Check readonly snapshots
        if (_readOnlySnapshotBundle.TryFindStorageNodes((Hash256AsKey)address, path, hash, out cached))
        {
            try
            {
                return RentRlpOrCopy(cached);
            }
            finally { cached.Dispose(); }
        }

        CappedArray<byte> buffer = _transientResource.BufferPool.Rent(RefCountingTrieNode.MaxEthereumBranchRlpLength);
        int len = _readOnlySnapshotBundle.TryLoadStorageRlpFromPersistence(address, path, hash, buffer.AsSpan(), flags);
        if (len > 0) return new CappedArray<byte>(buffer.UnderlyingArray!, len);
        _transientResource.BufferPool.Return(buffer);
        return default;
    }

    /// <summary>
    /// Loads and caches a state trie node for the warmer path. Returns a leased <see cref="RefCountingTrieNode"/>
    /// or <c>null</c> on miss. Checks transient cache -> main cache -> snapshots -> disk.
    /// </summary>
    public RefCountingTrieNode? LoadAndCacheStateNodeForWarmer(TreePath path, in ValueHash256 hash)
    {
        // Check transient child cache
        RefCountingTrieNode? node = _transientResource.TryGetStateNode(path, hash);
        if (node is not null)
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return node;
        }

        // Check main cache
        node = _trieNodeCache.TryGet(null, path, hash);
        if (node is not null)
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return node;
        }

        // Check changed RefCountingTrieNode dictionary
        if (_trieChanged && _changedStateNodes.TryGetValue(path, out RefCountingTrieNode? changedNode)
            && changedNode.Hash == hash && changedNode.TryAcquireLease())
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return changedNode;
        }

        // Check snapshots for RefCountingTrieNode
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStateNode(path, out RefCountingTrieNode? snapshotNode) && snapshotNode.Hash == hash && snapshotNode.TryAcquireLease())
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return snapshotNode;
            }
        }

        // Check ReadOnlySnapshotBundle snapshots
        Hash256 hashCommitment = hash.ToCommitment();
        if (_readOnlySnapshotBundle.TryFindStateNodes(path, hashCommitment, out RefCountingTrieNode? roNode))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return roNode;
        }

        // Fall back to disk
        byte[] rlpBuffer = new byte[RefCountingTrieNode.MaxEthereumBranchRlpLength];
        int rlpLen = _readOnlySnapshotBundle.TryLoadStateRlpForWarmer(path, hashCommitment, rlpBuffer, ReadFlags.None);
        if (rlpLen > 0 && rlpLen <= RefCountingTrieNode.MaxEthereumBranchRlpLength)
        {
            return _transientResource.SetAndLeaseStateNode(path, hash, rlpBuffer.AsSpan(0, rlpLen));
        }

        return null;
    }

    /// <summary>
    /// Loads and caches a storage trie node for the warmer path. Returns a leased <see cref="RefCountingTrieNode"/>
    /// or <c>null</c> on miss. Checks transient cache -> main cache -> snapshots -> disk.
    /// </summary>
    public RefCountingTrieNode? LoadAndCacheStorageNodeForWarmer(Hash256AsKey addressHash, TreePath path, in ValueHash256 hash)
    {
        RefCountingTrieNode? node = _transientResource.TryGetStorageNode(addressHash, path, hash);
        if (node is not null)
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return node;
        }

        node = _trieNodeCache.TryGet(addressHash, path, hash);
        if (node is not null)
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return node;
        }

        // Check changed RefCountingTrieNode dictionary
        if (_trieChanged && _changedStorageNodes.TryGetValue((addressHash, path), out RefCountingTrieNode? changedNode)
            && changedNode.Hash == hash && changedNode.TryAcquireLease())
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return changedNode;
        }

        // Check snapshots for RefCountingTrieNode
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStorageNode(addressHash, path, out RefCountingTrieNode? snapshotNode) && snapshotNode.Hash == hash && snapshotNode.TryAcquireLease())
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return snapshotNode;
            }
        }

        // Check ReadOnlySnapshotBundle snapshots
        Hash256 hashCommitment = hash.ToCommitment();
        if (_readOnlySnapshotBundle.TryFindStorageNodes(addressHash, path, hashCommitment, out RefCountingTrieNode? roNode))
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            return roNode;
        }

        // Fall back to disk
        byte[] rlpBuffer = new byte[RefCountingTrieNode.MaxEthereumBranchRlpLength];
        int rlpLen = _readOnlySnapshotBundle.TryLoadStorageRlpForWarmer(addressHash, path, hashCommitment, rlpBuffer, ReadFlags.None);
        if (rlpLen > 0 && rlpLen <= RefCountingTrieNode.MaxEthereumBranchRlpLength)
        {
            return _transientResource.SetAndLeaseStorageNode(addressHash, path, hash, rlpBuffer.AsSpan(0, rlpLen));
        }

        return null;
    }

    // This is called only during trie commit
    public void SetStateNode(in TreePath path, TrieNode newNode)
    {
        GuardDispose();
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        // Note: Hot path
        _trieChanged = true;
        if (newNode.Keccak is not null && newNode.FullRlp.IsNotNullOrEmpty)
        {
            RefCountingTrieNode rcNode = _transientResource.SetAndLeaseStateNode(path, newNode.Keccak, newNode.FullRlp.AsSpan());
            RefCountingTrieNode? old = _changedStateNodes.GetValueOrDefault(path);
            _changedStateNodes[path] = rcNode;
            old?.Dispose();
        }
    }

    // This is called only during trie commit
    public void SetStorageNode(Hash256 addr, in TreePath path, TrieNode newNode)
    {
        GuardDispose();
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        // Note: Hot path
        _trieChanged = true;
        if (newNode.Keccak is not null && newNode.FullRlp.IsNotNullOrEmpty)
        {
            RefCountingTrieNode rcNode = _transientResource.SetAndLeaseStorageNode(addr, path, newNode.Keccak, newNode.FullRlp.AsSpan());
            RefCountingTrieNode? old = _changedStorageNodes.GetValueOrDefault((addr, path));
            _changedStorageNodes[(addr, path)] = rcNode;
            old?.Dispose();
        }
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
            using ArrayPoolListRef<(Hash256AsKey, TreePath)> storageKeysToRemove = new(16);
            foreach (KeyValuePair<(Hash256AsKey, TreePath), RefCountingTrieNode> kv in _changedStorageNodes)
            {
                if (kv.Key.Item1.Value == addressHash)
                {
                    storageKeysToRemove.Add(kv.Key);
                }
            }

            foreach ((Hash256AsKey, TreePath) key in storageKeysToRemove)
            {
                if (_changedStorageNodes.TryRemove(key, out RefCountingTrieNode? removed))
                    removed.Dispose();
            }

            using ArrayPoolListRef<(AddressAsKey, UInt256)> slotKeysToRemove = new(16);
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
    /// <summary>The buffer pool from the transient resource, for callers that need to pass it to tree constructors.</summary>
    public PreallocatedCappedArrayPool BufferPool => _transientResource.BufferPool;

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
            _transientResource.Nodes.SetShardTrackers(_trieNodeCache.ShardTrackers);
            _trieChanged = false;

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

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true)) return;

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
