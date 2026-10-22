// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

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

    /// <summary>Starts reading persisted accounts and slots ahead of use; see <see cref="ReadOnlySnapshotBundle.Prefetch"/>.</summary>
    public void Prefetch(Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken) =>
        _readOnlySnapshotBundle.Prefetch(accounts, slots, cancellationToken);

    /// <summary>Passthrough of <see cref="ReadOnlySnapshotBundle.SupportsPrefetch"/>.</summary>
    public bool SupportsPrefetch => _readOnlySnapshotBundle.SupportsPrefetch;


    private SnapshotContent _currentPooledContent = null!;
    // These maps are direct reference from members in _currentPooledContent.
    private ConcurrentDictionary<HashedKey<Address>, Account?> _changedAccounts = null!;
    private ConcurrentDictionary<HashedKey<Address>, Account?> _readAccounts = null!;
    private ConcurrentDictionary<HashedKey<(Address, UInt256)>, UInt256?> _changedSlots = null!;
    private ConcurrentDictionary<Hash256, ulong> _changedStorageRoots = null!;
    private ConcurrentDictionary<HashedKey<Address>, bool> _selfDestructedAccountAddresses = null!;
    private ConcurrentDictionary<HashedKey<Address>, byte>? _addressesWithChangedSlots;

    // The cached resource holds some items that are pooled.
    // Notably, it holds loaded caches from trie warmer.
    private TransientResource _transientResource = null!;
    private WarmedNodeRecordBuffer? _cacheableNodeRecordBuffer;

    internal SnapshotPooledList _snapshots;
    private readonly INodeRecordBuffer? _nodeRecordBuffer;
    private bool _isDisposed;
    private readonly IResourcePool _resourcePool;

    internal ResourcePool.Usage _usage;

    // Slot reads that reach the wrapped bundle go through its negative filter. Only read-only execution opts in;
    // block processing keeps the plain loop, so its reads do not depend on the filter.
    private readonly bool _filterInMemorySlotReads;

    /// <param name="filterInMemorySlotReads">Serve slot reads with
    /// <see cref="ReadOnlySnapshotBundle.GetSlotFiltered"/>; for read-only execution only.</param>
    public SnapshotBundle(
        ReadOnlySnapshotBundle readOnlySnapshotBundle,
        INodeRecordBuffer? nodeRecordBuffer,
        IResourcePool resourcePool,
        ResourcePool.Usage usage,
        SnapshotPooledList? snapshots = null,
        bool filterInMemorySlotReads = false)
    {
        _readOnlySnapshotBundle = readOnlySnapshotBundle;
        _snapshots = snapshots ?? new SnapshotPooledList(1);
        _nodeRecordBuffer = nodeRecordBuffer;
        _resourcePool = resourcePool;
        _usage = usage;
        // A bundle that can never have a filter would only pay GetSlotFiltered's extra checks on every read.
        _filterInMemorySlotReads = filterInMemorySlotReads && readOnlySnapshotBundle.MayFilterSlots;

        _currentPooledContent = resourcePool.GetSnapshotContent(usage);
        _transientResource = resourcePool.GetCachedResource(usage);
        _cacheableNodeRecordBuffer = CreateCacheableNodeRecordBuffer(_transientResource);

        ExpandCurrentPooledContent();

        Metrics.ActiveSnapshotBundle++;
    }

    /// <summary>The buffer the trie nodes written through this bundle are recorded in, also resolving the nodes warmed for the current block; <c>null</c> for a trie-less bundle.</summary>
    /// <remarks>Replaced when <see cref="CollectAndApplySnapshot"/> retires the block's transient resource, so read it for each operation.</remarks>
    internal INodeRecordBuffer? NodeRecordBuffer => _cacheableNodeRecordBuffer;

    private WarmedNodeRecordBuffer? CreateCacheableNodeRecordBuffer(TransientResource transientResource) =>
        _nodeRecordBuffer is null ? null : new WarmedNodeRecordBuffer(_nodeRecordBuffer, transientResource.WarmedNodeBuffer);

    private void ExpandCurrentPooledContent()
    {
        _changedAccounts = _currentPooledContent.Accounts;
        _readAccounts = _currentPooledContent.ReadAccounts;
        _changedSlots = _currentPooledContent.Storages;
        _changedStorageRoots = _currentPooledContent.StorageRoots;
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

        if (_nodeRecordBuffer!.TryGetNode(_currentPooledContent.StateRootPointer, path, out TrieNode? node))
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

        return _readOnlySnapshotBundle.TryFindStateNodes(key, out node);
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash)
    {
        GuardDispose();

        if (_changedStorageRoots.TryGetValue(address, out ulong root) && root != 0
            && _nodeRecordBuffer!.TryGetNode(root, path, out TrieNode? node))
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

    /// <summary>Makes the final state trie readable from this bundle.</summary>
    internal void PublishBufferedStateRoot(in (ValueHash256 Hash, ulong Pointer) root)
    {
        GuardDispose();
        Debug.Assert(root.Pointer != 0 || root.Hash == ValueKeccak.EmptyTreeHash);
        _currentPooledContent.StateRootPointer = root.Pointer;
    }

    /// <summary>Makes a changed storage trie readable from this bundle, including a change to empty.</summary>
    internal void PublishBufferedStorageRoot(Hash256 address, in (ValueHash256 Hash, ulong Pointer) root)
    {
        GuardDispose();
        Debug.Assert(root.Pointer != 0 || root.Hash == ValueKeccak.EmptyTreeHash);
        _changedStorageRoots[address] = root.Pointer;
    }

    /// <summary>Finds a record by pointer in the trie node buffer or among the nodes warmed for the current block.</summary>
    internal bool TryLocate(ulong pointer, out NodeRecord record) => _cacheableNodeRecordBuffer!.TryLocate(pointer, out record);

    /// <summary>Loads a trie node the trie node buffer does not hold, for a bulk set or point read, as <see cref="INodeRecordStore.TryLoad"/> does.</summary>
    /// <remarks>
    /// Hands over the record warmed for this block at the position, otherwise the RLP in persistence, whose node is trusted to be the
    /// one asked for. The nodes of this bundle's changes and of every in-memory snapshot are in the buffer, which their pins keep
    /// readable, and <see cref="NodeRecordBuffer"/> resolves warmed nodes by pointer, so this source only fetches nodes the buffers
    /// reach by no pointer.
    /// </remarks>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    internal bool TryLoadTrieNodeRecord<TArg>(Hash256? address, in TreePath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
    {
        GuardDispose();

        if (!_transientResource.WarmedNodeBuffer.TryGet(address, path, out NodeRecord record, out _))
            return _readOnlySnapshotBundle.TryLoadPersistedRlp(address, path, ReadFlags.None, action, arg);

        action(record.Span, arg);
        return true;
    }

    /// <summary>Warms the nodes on the path to <paramref name="key"/> that the trie node buffer does not hold, for this block's bulk sets.</summary>
    /// <remarks>
    /// Reads only the warmed nodes and persistence, never this bundle's changes or snapshots, which the owner
    /// recycles while a warm-up runs. The transient resource holding the warmed nodes is leased for the walk, so it cannot be recycled
    /// until the walk ends. Does nothing once the bundle is being torn down.
    /// </remarks>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <param name="root">The trie's root before the block, as <see cref="NodeRecordBufferExtensions.ResolveRoot"/> returns it.</param>
    /// <param name="forDelete">Also warms the sibling needed when deleting the leaf collapses its branch.</param>
    internal void WarmUp(Hash256? address, (ValueHash256 Hash, ulong Pointer) root, in ValueHash256 key, bool forDelete)
    {
        TransientResource? transientResource = TryLeaseTransientResource();
        if (transientResource is null) return;

        try
        {
            BufferWarmUp.WarmUp(CreateCacheableNodeRecordBuffer(transientResource)!, transientResource.WarmedNodeBuffer, _readOnlySnapshotBundle, address, root, key, forDelete);
        }
        finally
        {
            transientResource.ReleaseLease();
        }
    }

    /// <summary>Captures a source for private trie updates that may outlive this bundle.</summary>
    internal BackgroundTrieSource CreateBackgroundTrieSource(Hash256 address)
    {
        GuardDispose();
        return new BackgroundTrieSource(_readOnlySnapshotBundle, _nodeRecordBuffer!, _snapshots, address);
    }

    /// <summary>A leased immutable source, independent of transient-resource retirement.</summary>
    /// <remarks>
    /// Used by <see cref="ScopeProvider.FlatStorageTree"/> to apply storage writes early on the idle thread; the commit then
    /// adopts the resulting root instead of bulk-setting those writes again. That apply runs off the owner thread and may
    /// still run after the block's transient resource is retired or this bundle is recycled, so unlike the bundle's own
    /// tries it cannot read the bundle or its per-block warmed nodes. It reads only the buffer, which its pins keep
    /// readable, and persistence through its own lease of the read-only bundle.
    /// </remarks>
    internal sealed class BackgroundTrieSource : INodeRecordStore, IDisposable
    {
        private readonly ReadOnlySnapshotBundle _readOnly;
        private readonly INodeRecordBuffer _buffer;
        private readonly Hash256 _address;
        private readonly INodeRecordBufferPin _pin;
        private readonly INodeRecordBufferPin? _snapshotPin;

        internal BackgroundTrieSource(ReadOnlySnapshotBundle readOnly, INodeRecordBuffer buffer, SnapshotPooledList snapshots, Hash256 address)
        {
            _readOnly = readOnly;
            _buffer = buffer;
            _address = address;
            readOnly.AcquireLease();
            try
            {
                _pin = buffer.PinHead();
                // The read-only lease protects its snapshots; the oldest local pin protects later local roots too.
                foreach (Snapshot snapshot in snapshots)
                {
                    if (snapshot.BufferPin is not { } pin) continue;
                    _snapshotPin = pin.Copy();
                    break;
                }
            }
            catch
            {
                readOnly.Dispose();
                _pin?.Dispose();
                throw;
            }
        }

        public bool TryLocate(ulong pointer, out NodeRecord record) => _buffer.TryLocate(pointer, out record);

        public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => _buffer.StartWriteBatch(alignedTotalLength);

        public bool TryLoad<TArg>(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            _readOnly.TryLoadPersistedRlp(_address, path, ReadFlags.None, action, arg);

        public void Dispose()
        {
            _snapshotPin?.Dispose();
            _pin.Dispose();
            _readOnly.Dispose();
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
        SetChangedSlotValue(address, index, value);
        RegisterAddressWithChangedSlots(address);
    }

    /// <summary>Like <see cref="SetChangedSlot"/>, but leaves registering the address to the caller.</summary>
    /// <remarks>The caller must also call <see cref="RegisterAddressWithChangedSlots"/> for <paramref name="address"/>,
    /// once after each <see cref="ClearStorage"/> of it or <see cref="CollectAndApplySnapshot"/>, or a later clear leaves
    /// the slot behind.</remarks>
    internal void SetChangedSlotValue(Address address, in UInt256 index, in UInt256 value)
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
    }

    internal void RegisterAddressWithChangedSlots(Address address)
    {
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

        _changedStorageRoots[addressHash] = 0;

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
    /// <param name="bufferPin">The pin covering the trie nodes written since the previous snapshot; the snapshot takes ownership.</param>
    public (Snapshot?, TransientResource?) CollectAndApplySnapshot(StateId from, StateId to, INodeRecordBufferPin? bufferPin, bool returnSnapshot = true)
    {
        // When assembling the snapshot, we straight up pass the _currentPooledContent into the new snapshot
        // This is because copying the values have a measurable impact on overall performance.
        Snapshot snapshot = new(
            from: from,
            to: to,
            content: _currentPooledContent,
            resourcePool: _resourcePool,
            usage: _usage,
            bufferPin: bufferPin);

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

    private void SwapTransientResource()
    {
        TransientResource transientResource = _resourcePool.GetCachedResource(_usage);
        _cacheableNodeRecordBuffer = CreateCacheableNodeRecordBuffer(transientResource);
        Volatile.Write(ref _transientResource, transientResource);
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
        _readAccounts = null!;
        _changedStorageRoots = null!;
        _selfDestructedAccountAddresses = null!;

        _resourcePool.ReturnSnapshotContent(_usage, _currentPooledContent);
        _transientResource.ReleaseLease();
        _readOnlySnapshotBundle.Dispose();

        Metrics.ActiveSnapshotBundle--;
    }
}
