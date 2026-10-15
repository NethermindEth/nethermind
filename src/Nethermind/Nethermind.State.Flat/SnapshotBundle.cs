// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;

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
    private ConcurrentDictionary<HashedKey<Address>, Account?> _readAccounts = null!;
    private ConcurrentDictionary<HashedKey<(Address, UInt256)>, UInt256?> _changedSlots = null!;
    private Dictionary<HashedKey<TreePath>, ulong> _changedStateNodes = null!;
    private AddressStorageNodeDictionary _changedStorageNodes = null!;
    private ConcurrentDictionary<HashedKey<Address>, bool> _selfDestructedAccountAddresses = null!;
    private ConcurrentDictionary<HashedKey<Address>, byte>? _addressesWithChangedSlots;

    private bool _trieChanged = false;

    // The cached resource holds some items that are pooled.
    // Notably, it holds loaded caches from trie warmer.
    private TransientResource _transientResource = null!;

    internal SnapshotPooledList _snapshots;
    private readonly ITrieNodeCache _trieNodeCache;
    private readonly TrieNodeLog? _trieNodeLog;
    private bool _isDisposed;
    private readonly IResourcePool _resourcePool;
    private static int _fallbackReadWarned;

    internal ResourcePool.Usage _usage;

    // Slot reads that reach the wrapped bundle go through its negative filter. Only read-only execution opts in;
    // block processing keeps the plain loop, so its reads do not depend on the filter.
    private readonly bool _filterInMemorySlotReads;

    /// <param name="filterInMemorySlotReads">Serve slot reads with
    /// <see cref="ReadOnlySnapshotBundle.GetSlotFiltered"/>; for read-only execution only.</param>
    public SnapshotBundle(
        ReadOnlySnapshotBundle readOnlySnapshotBundle,
        ITrieNodeCache trieNodeCache,
        TrieNodeLog? trieNodeLog,
        IResourcePool resourcePool,
        ResourcePool.Usage usage,
        SnapshotPooledList? snapshots = null,
        bool filterInMemorySlotReads = false)
    {
        _readOnlySnapshotBundle = readOnlySnapshotBundle;
        _snapshots = snapshots ?? new SnapshotPooledList(1);
        _trieNodeCache = trieNodeCache;
        _trieNodeLog = trieNodeLog;
        _resourcePool = resourcePool;
        _usage = usage;
        // A bundle that can never have a filter would only pay GetSlotFiltered's extra checks on every read.
        _filterInMemorySlotReads = filterInMemorySlotReads && readOnlySnapshotBundle.MayFilterSlots;

        _currentPooledContent = resourcePool.GetSnapshotContent(usage);
        _transientResource = resourcePool.GetCachedResource(usage);

        ExpandCurrentPooledContent();

        Metrics.ActiveSnapshotBundle++;
    }

    /// <summary>The log the trie nodes written through this bundle are recorded in; <c>null</c> for a trie-less bundle.</summary>
    internal TrieNodeLog? TrieNodeLog => _trieNodeLog;

    private void ExpandCurrentPooledContent()
    {
        _changedAccounts = _currentPooledContent.Accounts;
        _readAccounts = _currentPooledContent.ReadAccounts;
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

        if (!excludeChanged && (_changedAccounts.TryGetValue(key, out Account? acc) || _readAccounts.TryGetValue(key, out acc)))
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

    public void GetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out UInt256? value)
    {
        GuardDispose();

        HashedKey<(Address, UInt256)> key = new((address, index));

        if (_changedSlots.TryGetValue(key, out UInt256? slotValue))
        {
            value = slotValue;
            return;
        }

        // Self-destructed at the point of the latest change
        if (selfDestructStateIdx == _snapshots.Count + _readOnlySnapshotBundle.SnapshotCount)
        {
            value = null;
            return;
        }

        int currentBundleSelfDestructIdx = selfDestructStateIdx - _readOnlySnapshotBundle.SnapshotCount;
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStorage(key, out slotValue))
            {
                value = slotValue;
                return;
            }

            if (i <= currentBundleSelfDestructIdx)
            {
                // This is the snapshot with selfdestruct
                value = null;
                return;
            }
        }

        if (_filterInMemorySlotReads)
        {
            _readOnlySnapshotBundle.GetSlotFiltered(selfDestructStateIdx, key, out value);
        }
        else
        {
            _readOnlySnapshotBundle.GetSlot(selfDestructStateIdx, key, out value);
        }
    }

    public TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash)
    {
        GuardDispose();

        HashedKey<TreePath> key = new(path);

        TrieNode? node;
        if (_trieChanged && _changedStateNodes.TryGetValue(key, out ulong pointer))
        {
            node = _trieNodeLog!.GetRecord(pointer).ToTrieNode();
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

    // Returns a leased transient, or null once the bundle is being torn down. A stale read can acquire a
    // retired resource that was already re-rented by another bundle, so the acquire cannot be trusted on
    // its own: the identity re-check catches a resource swapped out by CollectAndApplySnapshot, and the
    // disposed re-check catches one released by Dispose, which leaves _transientResource pointing at the
    // recycled instance. The current resource always holds its owner lease (armed at pool checkout and
    // kept across SwapTransientResource) until Dispose, so the only way the acquire never succeeds is a
    // disposed bundle whose transient will not be replaced - the _isDisposed check bails there instead of
    // spinning forever (the target has no whole-bundle lease deferring that release).
    private TransientResource? TryLeaseTransientResource()
    {
        SpinWait spinWait = default;
        while (true)
        {
            if (Volatile.Read(ref _isDisposed)) return null;

            TransientResource transientResource = Volatile.Read(ref _transientResource);
            if (transientResource.TryAcquireLease())
            {
                if (ReferenceEquals(Volatile.Read(ref _transientResource), transientResource)
                    && !Volatile.Read(ref _isDisposed))
                {
                    return transientResource;
                }

                transientResource.ReleaseLease();
            }

            spinWait.SpinOnce();
        }
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

        if (_trieNodeCache.TryGet(null, path, hash.ValueHash256, out NodeRecordRef record))
        {
            node = record.ToTrieNode();
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
            return true;
        }

        return _readOnlySnapshotBundle.TryFindStateNodes(key, out node);
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash)
    {
        GuardDispose();

        HashedKey<(Hash256, TreePath)> key = new((address, path));

        TrieNode? node;
        if (_trieChanged && _changedStorageNodes.TryGetValue(key, out ulong pointer))
        {
            node = _trieNodeLog!.GetRecord(pointer).ToTrieNode();
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

        if (_trieNodeCache.TryGet(address, path, hash.ValueHash256, out NodeRecordRef record))
        {
            node = record.ToTrieNode();
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
            return true;
        }

        return _readOnlySnapshotBundle.TryFindStorageNodes(key, out node);
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

    /// <summary>Makes the nodes a bulk set wrote to the state trie readable from this bundle, in write order.</summary>
    internal void PublishLoggedStateNodes(WrittenNodes written)
    {
        GuardDispose();

        _trieChanged = true;
        ReadOnlySpan<(TreePath Path, ulong Pointer)> nodes = written.AsSpan();
        _changedStateNodes.EnsureCapacity(_changedStateNodes.Count + nodes.Length);
        foreach ((TreePath path, ulong pointer) in nodes) _changedStateNodes[path] = pointer;
    }

    internal AddressStorageNodeDictionary.AddressNodes GetStorageNodeDestination(Hash256 address) =>
        _changedStorageNodes.GetOrAddAddress(address);

    /// <summary>Makes the nodes a bulk set wrote to a storage trie readable from this bundle, in write order.</summary>
    /// <param name="nodes">The destination of the trie's address, from <see cref="GetStorageNodeDestination"/>.</param>
    internal void PublishLoggedStorageNodes(AddressStorageNodeDictionary.AddressNodes nodes, WrittenNodes written)
    {
        GuardDispose();

        _trieChanged = true;
        ReadOnlySpan<(TreePath Path, ulong Pointer)> writtenNodes = written.AsSpan();
        nodes.EnsureAdditionalCapacity(writtenNodes.Length);
        foreach ((TreePath path, ulong pointer) in writtenNodes) nodes.Set(path, pointer);
    }

    /// <summary>Loads the record of a trie node the trie node log does not hold, for a bulk set or point read.</summary>
    /// <remarks>
    /// Tries the nodes warmed for this block, then the trie node cache, then persistence, whose node is verified against
    /// <paramref name="hash"/>; either is added to the warmed nodes. The nodes of this bundle's changes and of every in-memory snapshot
    /// are in the log, which their retains keep readable, so they are looked up by path only as a last resort that counts
    /// <see cref="Metrics.TrieNodeLogFallbackReads"/>.
    /// </remarks>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    internal bool TryLoadTrieNodeRecord(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record)
    {
        GuardDispose();

        WarmedNodes warmed = _transientResource.WarmedNodes;
        if (warmed.TryGet(address, path, hash, out record)
            || TryWarmCachedRecord(warmed, address, path, hash, out record)
            || TryInsertPersistedRecord(warmed, address, path, hash, out record)) return true;

        if (!TryFindInMemoryRecord(_snapshots, address, path, hash, out record)
            && !TryFindInMemoryRecord(_readOnlySnapshotBundle.InMemorySnapshots, address, path, hash, out record)) return false;

        Metrics.IncrementTrieNodeLogFallbackReads();
        if (Interlocked.Exchange(ref _fallbackReadWarned, 1) == 0)
        {
            ILogger logger = Static.LogManager.GetClassLogger<SnapshotBundle>();
            if (logger.IsWarn) logger.Warn($"Trie node read by path from an in-memory snapshot instead of the trie node log. Address: {address}, path: {path}, hash: {hash}");
        }

        return true;
    }

    /// <summary>Warms the nodes on the path to <paramref name="key"/> that the trie node log does not hold, for this block's bulk sets.</summary>
    /// <remarks>
    /// Reads only the warmed nodes, the trie node cache and persistence, never this bundle's changes or snapshots, which the owner
    /// recycles while a warm-up runs. The transient resource holding the warmed nodes is leased for the walk, so retiring it waits
    /// for the walk to end. Does nothing once the bundle is being torn down.
    /// </remarks>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <param name="root">The trie's root before the block, as <see cref="LogBulkSet.ResolveRoot"/> returns it.</param>
    internal void WarmUp(Hash256? address, TrieRoot root, in ValueHash256 key)
    {
        TransientResource? transientResource = TryLeaseTransientResource();
        if (transientResource is null) return;

        try
        {
            LogBulkSet.WarmUp(_trieNodeLog!, new WarmUpRecordSource(this, transientResource, address), address, root, key);
        }
        finally
        {
            transientResource.ReleaseLease();
        }
    }

    /// <summary>Reads the node from the trie node cache into <paramref name="warmed"/>, so it is promoted to the cache again when the block retires.</summary>
    private bool TryWarmCachedRecord(WarmedNodes warmed, Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record)
    {
        if (!_trieNodeCache.TryGet(address, path, hash, out record)) return false;

        warmed.TryAdd(address, path, record, out _);
        return true;
    }

    /// <summary>Reads the node at <paramref name="path"/> from persistence into <paramref name="warmed"/>, if it hashes to <paramref name="hash"/>.</summary>
    private bool TryInsertPersistedRecord(WarmedNodes warmed, Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record)
    {
        record = default;
        bool inserted = false;
        PersistedNodeInsert insert = new(warmed, address, in path, in hash, ref record, ref inserted);
        if (address is null) _readOnlySnapshotBundle.TryLoadStateRlp(path, ReadFlags.None, PersistedNodeInsert.Insert, insert);
        else _readOnlySnapshotBundle.TryLoadStorageRlp(address, path, ReadFlags.None, PersistedNodeInsert.Insert, insert);
        return inserted;
    }

    private static bool TryFindInMemoryRecord(SnapshotPooledList snapshots, Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record)
    {
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            Snapshot snapshot = snapshots[i];
            bool found = address is null
                ? snapshot.TryGetStateNode(path, out ulong pointer)
                : snapshot.TryGetStorageNode((address, path), out pointer);
            if (found && snapshot.LogRetain?.Log.TryLocate(pointer, out record) == true && record.Hash == hash) return true;
        }

        record = default;
        return false;
    }

    /// <summary>The node source of a warm-up: the nodes already warmed, then the trie node cache, then the verified persisted node, warming the cached and persisted nodes it reads.</summary>
    private sealed class WarmUpRecordSource(SnapshotBundle bundle, TransientResource transientResource, Hash256? address) : INodeRecordSource
    {
        public bool TryLoad(in TreePath path, in ValueHash256 hash, out NodeRecordRef record) =>
            transientResource.WarmedNodes.TryGet(address, path, hash, out record)
            || bundle.TryWarmCachedRecord(transientResource.WarmedNodes, address, path, hash, out record)
            || bundle.TryInsertPersistedRecord(transientResource.WarmedNodes, address, path, hash, out record);
    }

    /// <summary>Inserts the persisted RLP handed to it into the warmed nodes, if it is the requested node.</summary>
    private readonly ref struct PersistedNodeInsert
    {
        public static readonly ReadOnlySpanAction<byte, PersistedNodeInsert> Insert =
            static (rlp, insert) => insert._inserted = insert._warmed.TryInsert(insert._address, insert._path, insert._hash, rlp, out insert._record);

        private readonly WarmedNodes _warmed;
        private readonly Hash256? _address;
        private readonly ref readonly TreePath _path;
        private readonly ref readonly ValueHash256 _hash;
        private readonly ref NodeRecordRef _record;
        private readonly ref bool _inserted;

        /// <param name="record">Set to the inserted record.</param>
        /// <param name="inserted">Set to whether the RLP was verified and inserted.</param>
        public PersistedNodeInsert(WarmedNodes warmed, Hash256? address, ref readonly TreePath path, ref readonly ValueHash256 hash,
            ref NodeRecordRef record, ref bool inserted)
        {
            _warmed = warmed;
            _address = address;
            _path = ref path;
            _hash = ref hash;
            _record = ref record;
            _inserted = ref inserted;
        }
    }

    public void SetAccount(Address address, Account? account) =>
        _changedAccounts[address] = account;

    internal void PromoteAccount(Address address, Account? account)
    {
        // ContainsKey is lock-free; TryAdd alone would take the bucket lock on every hot re-promote.
        HashedKey<Address> key = new(address);
        if (!_changedAccounts.ContainsKey(key) && !_readAccounts.ContainsKey(key))
        {
            _readAccounts.TryAdd(key, account);
        }
    }

    public void SetChangedSlot(Address address, in UInt256 index, in UInt256 value)
    {
        // So right now, if the value is zero, then it is a deletion. This is not the case with verkle where you
        // can set a value to be zero. Because of this distinction, the zerobytes logic is handled here instead of
        // lower down.
        HashedKey<(Address, UInt256)> key = new((address, index));
        if (value.IsZero)
        {
            _changedSlots[key] = null;
        }
        else
        {
            _changedSlots[key] = value;
        }

        ConcurrentDictionary<HashedKey<Address>, byte> addressesWithChangedSlots =
            Volatile.Read(ref _addressesWithChangedSlots) ?? CreateAddressesWithChangedSlots();
        if (!addressesWithChangedSlots.ContainsKey(address))
        {
            addressesWithChangedSlots.TryAdd(address, 0);
        }
    }

    private ConcurrentDictionary<HashedKey<Address>, byte> CreateAddressesWithChangedSlots()
    {
        ConcurrentDictionary<HashedKey<Address>, byte> created = new();
        return Interlocked.CompareExchange(ref _addressesWithChangedSlots, created, null) ?? created;
    }

    internal void ClearStorage(Address address, Hash256 addressHash)
    {
        GuardDispose();

        Account? account = DoGetAccount(address, excludeChanged: true, out _);
        bool isNewAccount = account == null || account.StorageRoot == Keccak.EmptyTreeHash;

        _selfDestructedAccountAddresses.TryAdd(address, isNewAccount);

        _changedStorageNodes.RemoveAddress(addressHash);

        // No dictionary yet means no slot of any address was written.
        if (Volatile.Read(ref _addressesWithChangedSlots)?.TryRemove(address, out _) != true)
        {
            return;
        }

        using ArrayPoolListRef<HashedKey<(Address, UInt256)>> slotKeysToRemove = new(16);
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> kvp in _changedSlots)
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

    // The trie warmer's PushSlotJob is slightly slow due to the wake up logic.
    // It is a net improvement to check and modify the bloom filter before calling the trie warmer push
    // as most of the slot should already be queued by prewarmer.
    //
    // The dedupe bloom lives on the recyclable _transientResource and these run on prewarmer/BAL threads
    // while the owner may retire that resource, so the read must pin it exactly like the warmer node reads
    // do - a bare field read would let the pool Reset/Dispose the BloomFilter (native memory) underneath.
    // A torn-down bundle declines the prewarm rather than warming into a recycled resource.
    public bool ShouldQueuePrewarm(Address address, UInt256? slot = null)
    {
        TransientResource? transientResource = TryLeaseTransientResource();
        if (transientResource is null) return false;

        try
        {
            return transientResource.ShouldPrewarm(address, slot);
        }
        finally
        {
            transientResource.ReleaseLease();
        }
    }

    /// <summary>
    /// Takes a lease on the underlying <see cref="ReadOnlySnapshotBundle"/> for the duration of a trie warmer traversal.
    /// </summary>
    /// <remarks>
    /// Warmer jobs race scope disposal by design; the managed fallout is caught in the warmer, but a read that is
    /// already inside the persistence reader when the last lease is released would touch a freed native RocksDB
    /// snapshot and crash the process. Holding a lease per in-flight traversal defers that release until the job ends.
    /// </remarks>
    /// <returns><c>false</c> when the bundle is already fully disposed; the caller must skip the traversal.</returns>
    internal bool TryLeaseReadOnlyBundle() => _readOnlySnapshotBundle.TryLease();

    /// <summary>Releases a lease taken with <see cref="TryLeaseReadOnlyBundle"/>.</summary>
    internal void ReleaseReadOnlyBundleLease() => _readOnlySnapshotBundle.Dispose();

    /// <summary>Turns the changes written since the previous snapshot into a new snapshot and starts collecting the next one.</summary>
    /// <param name="logRetain">The retain covering the trie nodes written since the previous snapshot; the snapshot takes ownership.</param>
    public (Snapshot?, TransientResource?) CollectAndApplySnapshot(StateId from, StateId to, TrieNodeLogRetain? logRetain, bool returnSnapshot = true)
    {
        // When assembling the snapshot, we straight up pass the _currentPooledContent into the new snapshot
        // This is because copying the values have a measurable impact on overall performance.
        Snapshot snapshot = new(
            from: from,
            to: to,
            content: _currentPooledContent,
            resourcePool: _resourcePool,
            usage: _usage)
        {
            LogRetain = logRetain
        };

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
            SnapshotContent committedContent = _currentPooledContent;
            _currentPooledContent = _resourcePool.GetSnapshotContent(_usage);
            ExpandCurrentPooledContent();
            _addressesWithChangedSlots?.NoLockClear();
            DropReadAccounts(committedContent);

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
            _addressesWithChangedSlots?.NoLockClear();
            _trieChanged = false;

            return (null, null);
        }
    }

    /// <summary>Empties the read cache of content that was just handed to a snapshot.</summary>
    /// <remarks>
    /// A snapshot never reads these entries, so keeping them would only hold memory that its estimate does not count.
    /// The clear takes the stripe locks because a warmer can still be promoting into this content, and it keeps the
    /// grown table for the next block that reuses the pooled content.
    /// </remarks>
    private static void DropReadAccounts(SnapshotContent content) => content.ReadAccounts.NoResizeClear();

    private void SwapTransientResource() =>
        Volatile.Write(ref _transientResource, _resourcePool.GetCachedResource(_usage));

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true)) return;

        _snapshots.Dispose();

        // Null them in case unexpected mutation from trie warmer
        _snapshots = null!;
        _changedSlots = null!;
        _changedAccounts = null!;
        _readAccounts = null!;
        _changedStorageNodes = null!;
        _selfDestructedAccountAddresses = null!;

        _resourcePool.ReturnSnapshotContent(_usage, _currentPooledContent);
        _transientResource.ReleaseLease();
        _readOnlySnapshotBundle.Dispose();

        Metrics.ActiveSnapshotBundle--;
    }
}
