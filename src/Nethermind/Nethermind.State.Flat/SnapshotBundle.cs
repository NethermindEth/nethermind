// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A mutable bundle wrapping a <see cref="ReadOnlySnapshotBundle"/> with a write buffer backed by <see cref="SnapshotContent"/>.
/// </summary>
public sealed class SnapshotBundle : IDisposable
{
    private readonly ReadOnlySnapshotBundle _readOnlySnapshotBundle;

    /// <summary>
    /// Passthrough of <see cref="ReadOnlySnapshotBundle.IsHistorical"/>: the wrapped bundle is history-backed and
    /// trie-less, so the scope must skip post-block state-root trie recomputation.
    /// </summary>
    public bool IsHistorical => _readOnlySnapshotBundle.IsHistorical;


    private SnapshotContent _currentPooledContent = null!;
    // These maps are direct reference from members in _currentPooledContent.
    private ConcurrentDictionary<HashedKey<Address>, Account?> _changedAccounts = null!;
    private ConcurrentDictionary<HashedKey<(Address, UInt256)>, SlotValue?> _changedSlots = null!;
    private Dictionary<HashedKey<TreePath>, TrieNode> _changedStateNodes = null!;
    private AddressStorageNodeDictionary _changedStorageNodes = null!;
    private ConcurrentDictionary<HashedKey<Address>, bool> _selfDestructedAccountAddresses = null!;
    private readonly ConcurrentDictionary<HashedKey<Address>, byte> _addressesWithChangedSlots = new();

    private bool _trieChanged = false;

    // The cached resource holds some items that are pooled.
    // Notably, it holds loaded caches from trie warmer.
    private TransientResource _transientResource = null!;

    internal SnapshotPooledList _snapshots;
    private readonly ITrieNodeCache _trieNodeCache;

    // Incrementing this invalidates queued warmer jobs, including jobs owned by leased warmup sessions.
    private volatile int _hintSequenceId;
    private bool _isDisposed;
    private readonly Lock _warmupSessionLock = new();
    private FlatTrieWarmupSession? _warmupSession;
    private bool _warmingStopped;
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

    public Account? GetAccount(Address address) => DoGetAccount(address, excludeChanged: false, out _);

    internal Account? GetAccount(Address address, out bool isInCurrentSnapshot) =>
        DoGetAccount(address, excludeChanged: false, out isInCurrentSnapshot);

    private Account? DoGetAccount(Address address, bool excludeChanged, out bool isInCurrentSnapshot)
    {
        GuardDispose();

        HashedKey<Address> key = new(address);

        if (!excludeChanged && _changedAccounts.TryGetValue(key, out Account? acc))
        {
            isInCurrentSnapshot = true;
            return acc;
        }

        isInCurrentSnapshot = false;

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetAccount(key, out acc))
            {
                return acc;
            }
        }

        return _readOnlySnapshotBundle.GetAccount(address, key);
    }

    public int DetermineSelfDestructSnapshotIdx(Address address)
    {
        HashedKey<Address> key = new(address);

        if (_selfDestructedAccountAddresses.ContainsKey(key)) return _snapshots.Count + _readOnlySnapshotBundle.SnapshotCount;

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].HasSelfDestruct(key)) return i + _readOnlySnapshotBundle.SnapshotCount;
        }

        return _readOnlySnapshotBundle.DetermineSelfDestructSnapshotIdx(address);
    }

    public byte[]? GetSlot(Address address, in UInt256 index, int selfDestructStateIdx)
    {
        GuardDispose();

        HashedKey<(Address, UInt256)> key = new((address, index));

        if (_changedSlots.TryGetValue(key, out SlotValue? slotValue))
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
            if (_snapshots[i].TryGetStorage(key, out slotValue))
            {
                return slotValue?.ToEvmBytes();
            }

            if (i <= currentBundleSelfDestructIdx)
            {
                // This is the snapshot with selfdestruct
                return null;
            }
        }

        return _readOnlySnapshotBundle.GetSlot(selfDestructStateIdx, key);
    }

    public TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash)
    {
        GuardDispose();

        HashedKey<TreePath> key = new(path);

        if (_trieChanged && _changedStateNodes.TryGetValue(key, out TrieNode? node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
        }
        else if (DoFindStateNodeExternal(path, hash, out node))
        {
        }
        else
        {
            node = new TrieNode(NodeType.Unknown, hash);
        }

        return node;
    }

    private bool DoFindStateNodeExternal(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        HashedKey<TreePath> key = new(path);
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStateNode(key, out node))
            {
                Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
                return true;
            }
        }

        if (_readOnlySnapshotBundle.TryFindStateNodes(key, out node)) return true;

        if (_transientResource.TryGetStateNode(path, hash, out node) || _trieNodeCache.TryGet(null, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
            return true;
        }

        return false;
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash)
    {
        GuardDispose();

        HashedKey<(Hash256, TreePath)> key = new((address, path));

        if (_trieChanged && _changedStorageNodes.TryGetValue(key, out TrieNode? node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
        }
        else if (DoTryFindStorageNodeExternal(address, path, hash, out node) && node is not null)
        {
        }
        else
        {
            node = new TrieNode(NodeType.Unknown, hash);
        }

        return node;
    }


    // Note: No self-destruct boundary check needed for trie nodes. Trie iteration starts from the storage root hash,
    // so if storage was self-destructed, the new root is different and orphaned nodes won't be traversed. So we skip the
    // check for slightly improved latency.
    private bool DoTryFindStorageNodeExternal(Hash256 address, in TreePath path, Hash256 hash, out TrieNode? node)
    {
        HashedKey<(Hash256, TreePath)> key = new((address, path));
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStorageNode(key, out node))
            {
                Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
                return true;
            }
        }

        if (_readOnlySnapshotBundle.TryFindStorageNodes(key, out node)) return true;

        if (_transientResource.TryGetStorageNode(address, path, hash, out node) || _trieNodeCache.TryGet(address, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
            return true;
        }

        return false;
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
        _trieChanged = true;
        _changedStateNodes[path] = newNode;
        _transientResource.UpdateStateNode(path, newNode);
    }

    internal void PublishStateNodes(IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers)
    {
        _changedStateNodes.EnsureCapacity(_changedStateNodes.Count + CountBufferedNodes(buffers));

        foreach (List<(TreePath Path, TrieNode Node)> buffer in buffers)
        {
            foreach ((TreePath path, TrieNode node) in buffer) SetStateNode(path, node);
        }
    }

    internal AddressStorageNodeDictionary.AddressNodes GetStorageNodeDestination(Hash256 address) =>
        _changedStorageNodes.GetOrAddAddress(address);

    // This is called only during trie commit
    public void SetStorageNode(Hash256 addr, in TreePath path, TrieNode newNode)
    {
        GuardDispose();
        SetStorageNode(GetStorageNodeDestination(addr), addr, path, newNode);
    }

    internal void SetStorageNode(
        AddressStorageNodeDictionary.AddressNodes nodes,
        Hash256 addr,
        in TreePath path,
        TrieNode newNode)
    {
        GuardDispose();
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        // Note: Hot path
        _trieChanged = true;
        nodes.Set(path, newNode);
        _transientResource.UpdateStorageNode(addr, path, newNode);
    }

    internal void PublishStorageNodes(
        AddressStorageNodeDictionary.AddressNodes nodes,
        Hash256 address,
        IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers)
    {
        nodes.EnsureAdditionalCapacity(CountBufferedNodes(buffers));

        foreach (List<(TreePath Path, TrieNode Node)> buffer in buffers)
        {
            foreach ((TreePath path, TrieNode node) in buffer) SetStorageNode(nodes, address, path, node);
        }
    }

    private static int CountBufferedNodes(IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers)
    {
        int count = 0;
        foreach (List<(TreePath Path, TrieNode Node)> buffer in buffers) count += buffer.Count;
        return count;
    }

    public void SetAccount(Address address, Account? account) =>
        _changedAccounts[address] = account;

    internal void PromoteAccount(Address address, Account? account)
    {
        // ContainsKey is lock-free; TryAdd alone would take the bucket lock on every hot re-promote.
        HashedKey<Address> key = new(address);
        if (!_changedAccounts.ContainsKey(key))
        {
            _changedAccounts.TryAdd(key, account);
        }
    }

    public void SetChangedSlot(Address address, in UInt256 index, byte[] value)
    {
        // So right now, if the value is zero, then it is a deletion. This is not the case with verkle where you
        // can set a value to be zero. Because of this distinction, the zerobytes logic is handled here instead of
        // lower down.
        HashedKey<(Address, UInt256)> key = new((address, index));
        if (value is null || Bytes.AreEqual(value, StorageTree.ZeroBytes))
        {
            _changedSlots[key] = null;
        }
        else
        {
            _changedSlots[key] = SlotValue.FromSpanWithoutLeadingZero(value);
        }

        if (!_addressesWithChangedSlots.ContainsKey(address))
        {
            _addressesWithChangedSlots.TryAdd(address, 0);
        }
    }

    internal void ClearStorage(Address address, Hash256 addressHash)
    {
        GuardDispose();

        Account? account = DoGetAccount(address, excludeChanged: true, out _);
        bool isNewAccount = account == null || account.StorageRoot == Keccak.EmptyTreeHash;

        _selfDestructedAccountAddresses.TryAdd(address, isNewAccount);

        _changedStorageNodes.RemoveAddress(addressHash);

        if (!_addressesWithChangedSlots.TryRemove(address, out _))
        {
            return;
        }

        using ArrayPoolListRef<HashedKey<(Address, UInt256)>> slotKeysToRemove = new(16);
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> kvp in _changedSlots)
        {
            if (kvp.Key.Key.Item1 == address)
            {
                slotKeysToRemove.Add(kvp.Key);
            }
        }

        foreach (HashedKey<(Address, UInt256)> key in slotKeysToRemove)
        {
            _changedSlots.TryRemove(key, out _);
        }
    }

    internal int HintSequenceId => _hintSequenceId;

    internal void StopWarming()
    {
        lock (_warmupSessionLock)
        {
            _warmingStopped = true;
            Interlocked.Increment(ref _hintSequenceId);
            _warmupSession?.StopWarming();
        }
    }

    internal IWorldStateScopeProvider.ITrieWarmupSession CreateTrieWarmupSession(
        in StateId baseState,
        ITrieWarmer trieWarmer,
        ILogManager logManager)
    {
        lock (_warmupSessionLock)
        {
            if (_isDisposed || IsHistorical || trieWarmer is NoopTrieWarmer)
                return IWorldStateScopeProvider.ITrieWarmupSession.Noop.Instance;

            if (_warmupSession is null)
            {
                if (_warmingStopped) return IWorldStateScopeProvider.ITrieWarmupSession.Noop.Instance;
                if (!_readOnlySnapshotBundle.TryLease()) throw new ObjectDisposedException(nameof(SnapshotBundle));
                TransientResource transientResource = _transientResource;
                bool transientLeased = false;
                SnapshotPooledList initialSnapshots = new(_snapshots.Count);
                try
                {
                    transientLeased = transientResource.TryAcquireLease();
                    if (!transientLeased) throw new ObjectDisposedException(nameof(SnapshotBundle));
                    foreach (Snapshot snapshot in _snapshots)
                    {
                        snapshot.AcquireLease();
                        initialSnapshots.Add(snapshot);
                    }
                    _warmupSession = new FlatTrieWarmupSession(
                        baseState, this, _readOnlySnapshotBundle, initialSnapshots, transientResource, _trieNodeCache, trieWarmer, logManager);
                }
                catch
                {
                    initialSnapshots.Dispose();
                    if (transientLeased) transientResource.ReleaseLease();
                    _readOnlySnapshotBundle.Dispose();
                    throw;
                }
            }

            _warmupSession.AcquireLease();
            return _warmupSession;
        }
    }

    private void ReleaseWarmupSession()
    {
        FlatTrieWarmupSession? session;
        lock (_warmupSessionLock)
        {
            session = _warmupSession;
            _warmupSession = null;
        }
        session?.Dispose();
    }

    public (Snapshot?, TransientResource?) CollectAndApplySnapshot(StateId from, StateId to, bool returnSnapshot = true)
    {
        StopWarming();

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

            SwapTransientResource();
            _trieChanged = false;

            // Make and apply new snapshot content.
            _currentPooledContent = _resourcePool.GetSnapshotContent(_usage);
            ExpandCurrentPooledContent();
            _addressesWithChangedSlots.NoLockClear();

            return (snapshot, transientResource);
        }
        else
        {
            snapshot.Dispose(); // Revert the lease before

            TransientResource retired = _transientResource;
            SwapTransientResource();
            retired.ReleaseLease();

            _currentPooledContent = _resourcePool.GetSnapshotContent(_usage);
            ExpandCurrentPooledContent();
            _addressesWithChangedSlots.NoLockClear();
            _trieChanged = false;

            return (null, null);
        }
    }

    private void SwapTransientResource() =>
        Volatile.Write(ref _transientResource, _resourcePool.GetCachedResource(_usage));

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true)) return;

        StopWarming();
        ReleaseWarmupSession();
        _snapshots.Dispose();

        // Null them in case unexpected mutation from trie warmer
        _snapshots = null!;
        _changedSlots = null!;
        _changedAccounts = null!;
        _changedStorageNodes = null!;
        _selfDestructedAccountAddresses = null!;

        _resourcePool.ReturnSnapshotContent(_usage, _currentPooledContent);
        _transientResource.ReleaseLease();
        _readOnlySnapshotBundle.Dispose();

        Metrics.ActiveSnapshotBundle--;
    }
}
