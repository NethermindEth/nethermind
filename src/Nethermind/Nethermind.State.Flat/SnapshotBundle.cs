// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A mutable bundle wrapping a <see cref="ReadOnlySnapshotBundle"/> with a write buffer backed by <see cref="SnapshotContent"/>.
/// </summary>
public sealed class SnapshotBundle : IDisposable
{
    private sealed class Lease(SnapshotBundle bundle) : RefCountingDisposable
    {
        public bool TryAcquire() => TryAcquireLease();
        protected override void CleanUp() => bundle.CleanUp();
    }

    private readonly ReadOnlySnapshotBundle _readOnlySnapshotBundle;
    private readonly Lease _lease;
    private bool _ownerLeaseReleased;

    private SnapshotContent _currentPooledContent = null!;
    // These maps are direct reference from members in _currentPooledContent.
    private ConcurrentDictionary<HashedKey<Address>, Account?> _changedAccounts = null!;
    private ConcurrentDictionary<HashedKey<(Address, UInt256)>, SlotValue?> _changedSlots = null!;
    private Dictionary<HashedKey<TreePath>, TrieNode> _changedStateNodes = null!;
    // The object-backed tier is always present; when _flatNodeStorage is set the slab-backed tier is
    // used instead and _changedStorageNodes stays an empty, unused reference.
    private AddressStorageNodeDictionary _changedStorageNodes = null!;
    private FlatAddressStorageNodeDictionary? _changedFlatStorageNodes;
    private bool _flatNodeStorage;
    private ConcurrentDictionary<HashedKey<Address>, bool> _selfDestructedAccountAddresses = null!;

    private bool _trieChanged = false;

    // The cached resource holds some items that are pooled.
    // Notably, it holds loaded caches from trie warmer.
    private TransientResource _transientResource = null!;

    // Ambient per-job capture of the pinned transient resource. A warmer traversal runs synchronously
    // on one thread and reads the transient once per node through a shared adapter that cannot carry a
    // parameter. The owning job pins the transient once (see EnterWarmerTransientScope) and parks it here,
    // so the per-node reads use the pinned reference directly with no per-node lease atomics. The owner
    // reference gates the slot so a foreign bundle can never read another bundle's capture.
    [ThreadStatic]
    private static (SnapshotBundle Owner, TransientResource Resource) t_warmerJobCapture;

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
        _lease = new Lease(this);

        _currentPooledContent = resourcePool.GetSnapshotContent(usage);
        _transientResource = resourcePool.GetCachedResource(usage);
        _transientResource.OnRented(resourcePool, usage);

        ExpandCurrentPooledContent();

        Metrics.ActiveSnapshotBundle++;
    }

    private void ExpandCurrentPooledContent()
    {
        _changedAccounts = _currentPooledContent.Accounts;
        _changedSlots = _currentPooledContent.Storages;
        _changedStorageNodes = _currentPooledContent.StorageNodes;
        _changedFlatStorageNodes = _currentPooledContent.FlatStorageNodes;
        _flatNodeStorage = _currentPooledContent.StorageNodesAreFlat;
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
        else if (_transientResource.TryGetStateNode(path, hash, out node))
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

    public TrieNode FindStateNodeOrUnknownForTrieWarmer(in TreePath path, Hash256 hash)
    {
        // TrieWarmer only touch `_transientResource`
        GuardDispose();

        (SnapshotBundle owner, TransientResource resource) = t_warmerJobCapture;
        if (ReferenceEquals(owner, this))
        {
            // The enclosing warmer job already pinned the transient for its whole traversal.
            return WarmUpStateNode(resource, path, hash);
        }

        // Standalone caller (e.g. tests) with no per-job capture: pin per read.
        TransientResource transientResource = LeaseTransientResourceForWarmer();
        try
        {
            return WarmUpStateNode(transientResource, path, hash);
        }
        finally
        {
            transientResource.ReleaseLease();
        }
    }

    private TrieNode WarmUpStateNode(TransientResource transientResource, in TreePath path, Hash256 hash)
    {
        if (transientResource.TryGetStateNode(path, hash, out TrieNode? node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
        }
        else
        {
            node = transientResource.GetOrAddStateNode(path,
                DoFindStateNodeExternal(path, hash, out node)
                    ? node
                    : new TrieNode(NodeType.Unknown, hash));
        }

        return node;
    }

    // Terminates because the caller holds a bundle lease, which keeps the current resource's owner
    // lease held. A stale read can acquire a retired resource that was already re-rented by another
    // bundle, so the identity re-check below is required before trusting the acquire.
    private TransientResource LeaseTransientResourceForWarmer()
    {
        SpinWait spinWait = default;
        while (true)
        {
            TransientResource transientResource = Volatile.Read(ref _transientResource);
            if (transientResource.TryAcquireLease())
            {
                if (ReferenceEquals(Volatile.Read(ref _transientResource), transientResource)) return transientResource;
                transientResource.ReleaseLease();
            }

            spinWait.SpinOnce();
        }
    }

    /// <summary>
    /// Pins the transient resource once for the whole duration of a trie-warmer traversal and parks it in
    /// <see cref="t_warmerJobCapture"/>, so the per-node warmer reads below skip the per-node lease atomics
    /// and read the pinned resource directly. The single acquire keeps the ABA identity re-check; the reader
    /// lease keeps the captured resource alive across a concurrent <see cref="SwapTransientResource"/> until
    /// the returned handle is disposed. Callers must already hold the bundle lease (<see cref="TryLease"/>).
    /// </summary>
    internal WarmerTransientLease EnterWarmerTransientScope()
    {
        TransientResource captured = LeaseTransientResourceForWarmer();
        (SnapshotBundle Owner, TransientResource Resource) previous = t_warmerJobCapture;
        t_warmerJobCapture = (this, captured);
        // Invariant: the per-node warmer's in-place GetOrAdd into this captured resource may race a
        // concurrent Commit->PopulateTrieNodeCache enumeration of the same shards. Tolerated by design --
        // a torn tuple read yields at worst a misplaced/lost cache entry (Keccak-validated on read -> DB
        // fallback), never a wrong node, and the lease pins the resource so the shards are not reallocated.
        return new WarmerTransientLease(captured, previous);
    }

    internal readonly struct WarmerTransientLease(
        TransientResource captured,
        (SnapshotBundle Owner, TransientResource Resource) previous) : IDisposable
    {
        public void Dispose()
        {
            t_warmerJobCapture = previous;
            captured.ReleaseLease();
        }
    }

    private bool DoFindStateNodeExternal(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        if (_trieNodeCache.TryGet(null, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
            return true;
        }

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

        HashedKey<(Hash256, TreePath)> key = new((address, path));

        if (_trieChanged && TryGetChangedStorageNode(key, out TrieNode? node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
        }
        else if (_transientResource.TryGetStorageNode((Hash256AsKey)address, path, hash, out node))
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

    private bool TryGetChangedStorageNode(HashedKey<(Hash256, TreePath)> key, [NotNullWhen(true)] out TrieNode? node) =>
        _flatNodeStorage
            ? _changedFlatStorageNodes!.TryGetValue(key, out node)
            : _changedStorageNodes.TryGetValue(key, out node);


    public TrieNode FindStorageNodeOrUnknownTrieWarmer(Hash256 address, in TreePath path, Hash256 hash)
    {
        GuardDispose();

        (SnapshotBundle owner, TransientResource resource) = t_warmerJobCapture;
        if (ReferenceEquals(owner, this))
        {
            // The enclosing warmer job already pinned the transient for its whole traversal.
            return WarmUpStorageNode(resource, address, path, hash);
        }

        // Standalone caller (e.g. tests) with no per-job capture: pin per read.
        TransientResource transientResource = LeaseTransientResourceForWarmer();
        try
        {
            return WarmUpStorageNode(transientResource, address, path, hash);
        }
        finally
        {
            transientResource.ReleaseLease();
        }
    }

    private TrieNode WarmUpStorageNode(TransientResource transientResource, Hash256 address, in TreePath path, Hash256 hash)
    {
        if (transientResource.TryGetStorageNode((Hash256AsKey)address, path, hash, out TrieNode? node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
        }
        else
        {
            node = transientResource.GetOrAddStorageNode((Hash256AsKey)address, path,
                DoTryFindStorageNodeExternal(address, path, hash, out node) && node is not null
                    ? node
                    : new TrieNode(NodeType.Unknown, hash));
        }

        return node;
    }

    // Note: No self-destruct boundary check needed for trie nodes. Trie iteration starts from the storage root hash,
    // so if storage was self-destructed, the new root is different and orphaned nodes won't be traversed. So we skip the
    // check for slightly improved latency.
    private bool DoTryFindStorageNodeExternal(Hash256 address, in TreePath path, Hash256 hash, out TrieNode? node)
    {
        if (_trieNodeCache.TryGet(address, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
            return true;
        }

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

    /// <summary>
    /// A per-address storage-node write target captured once per storage-trie commit. Holds the
    /// object-backed or slab-backed inner store, whichever is live, so the per-node write path stays a
    /// direct concrete call rather than an interface dispatch on the default (object-backed) path.
    /// </summary>
    internal readonly struct StorageNodeDestination
    {
        private readonly AddressStorageNodeDictionary.AddressNodes? _nodes;
        private readonly FlatAddressStorageNodeDictionary.FlatAddressNodes? _flat;

        internal StorageNodeDestination(AddressStorageNodeDictionary.AddressNodes nodes)
        {
            _nodes = nodes;
            _flat = null;
        }

        internal StorageNodeDestination(FlatAddressStorageNodeDictionary.FlatAddressNodes flat)
        {
            _flat = flat;
            _nodes = null;
        }

        internal void Set(in TreePath path, TrieNode node)
        {
            if (_flat is not null) _flat.Set(path, node);
            else _nodes!.Set(path, node);
        }

        internal void EnsureAdditionalCapacity(int additionalCapacity)
        {
            if (_flat is not null) _flat.EnsureAdditionalCapacity(additionalCapacity);
            else _nodes!.EnsureAdditionalCapacity(additionalCapacity);
        }
    }

    internal StorageNodeDestination GetStorageNodeDestination(Hash256 address) =>
        _flatNodeStorage
            ? new StorageNodeDestination(_changedFlatStorageNodes!.GetOrAddAddress(address))
            : new StorageNodeDestination(_changedStorageNodes.GetOrAddAddress(address));

    // This is called only during trie commit
    public void SetStorageNode(Hash256 addr, in TreePath path, TrieNode newNode)
    {
        GuardDispose();
        SetStorageNode(GetStorageNodeDestination(addr), addr, path, newNode);
    }

    internal void SetStorageNode(
        in StorageNodeDestination nodes,
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
        in StorageNodeDestination nodes,
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

    internal void PromoteAccount(Address address, Account? account) =>
        _changedAccounts.TryAdd(address, account);

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
    }

    // Also called SelfDestruct
    public void Clear(Address address, Hash256 addressHash)
    {
        GuardDispose();

        Account? account = DoGetAccount(address, excludeChanged: true, out _);
        // So... a clear is always sent even on a new account. This makes is a minor optimization as
        // it skips persistence, but probably need to make sure it does not send it at all in the first place.
        bool isNewAccount = account == null || account.StorageRoot == Keccak.EmptyTreeHash;

        _selfDestructedAccountAddresses.TryAdd(address, isNewAccount);

        if (!isNewAccount)
        {
            if (_flatNodeStorage) _changedFlatStorageNodes!.RemoveAddress(addressHash);
            else _changedStorageNodes.RemoveAddress(addressHash);

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
    }

    // The trie warmer's PushSlotJob is slightly slow due to the wake up logic.
    // It is a net improvement to check and modify the bloom filter before calling the trie warmer push
    // as most of the slot should already be queued by prewarmer.
    public bool ShouldQueuePrewarm(Address address, UInt256? slot = null) => _transientResource.ShouldPrewarm(address, slot);

    public bool ShouldQueuePrewarm(in ValueAddress address, UInt256? slot = null) => _transientResource.ShouldPrewarm(address, slot);

    /// <summary>
    /// Takes a lease on this bundle for the duration of a trie warmer traversal, covering the snapshot
    /// contents, the pooled write buffer, the transient resource and the underlying
    /// <see cref="ReadOnlySnapshotBundle"/>.
    /// </summary>
    /// <remarks>
    /// Warmer jobs race scope disposal by design; the managed fallout is caught in the warmer, but a read that
    /// overlaps the teardown would observe pooled contents being reset or re-rented (lost writes read back as
    /// zero), and a read already inside the persistence reader when the last lease is released would touch a
    /// freed native RocksDB snapshot and crash the process. Holding a lease per in-flight traversal defers the
    /// whole teardown until the job ends.
    /// </remarks>
    /// <returns><c>false</c> when the bundle is already fully disposed; the caller must skip the traversal.</returns>
    internal bool TryLease()
    {
        if (!_lease.TryAcquire()) return false;
        if (!_readOnlySnapshotBundle.TryLease())
        {
            _lease.Dispose();
            return false;
        }

        return true;
    }

    /// <summary>Releases a lease taken with <see cref="TryLease"/>.</summary>
    internal void ReleaseLease()
    {
        _readOnlySnapshotBundle.Dispose();
        _lease.Dispose();
    }

    /// <summary>
    /// Takes a lease on the underlying <see cref="ReadOnlySnapshotBundle"/> only, for callers that need
    /// just the native persistence reader; warmer traversals must use <see cref="TryLease"/> instead.
    /// </summary>
    internal bool TryLeaseReadOnlyBundle() => _readOnlySnapshotBundle.TryLease();

    /// <summary>Releases a lease taken with <see cref="TryLeaseReadOnlyBundle"/>.</summary>
    internal void ReleaseReadOnlyBundleLease() => _readOnlySnapshotBundle.Dispose();

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

            SwapTransientResource();
            _trieChanged = false;

            // Make and apply new snapshot content.
            _currentPooledContent = _resourcePool.GetSnapshotContent(_usage);
            ExpandCurrentPooledContent();

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
            _trieChanged = false;

            return (null, null);
        }
    }

    private void SwapTransientResource()
    {
        TransientResource fresh = _resourcePool.GetCachedResource(_usage);
        fresh.OnRented(_resourcePool, _usage);
        Volatile.Write(ref _transientResource, fresh);
    }

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ownerLeaseReleased, true)) return;

        _lease.Dispose();
    }

    private void CleanUp()
    {
        _isDisposed = true;

        _snapshots.Dispose();

        // Null them in case unexpected mutation from trie warmer
        _snapshots = null!;
        _changedSlots = null!;
        _changedAccounts = null!;
        _changedStorageNodes = null!;
        _changedFlatStorageNodes = null;
        _selfDestructedAccountAddresses = null!;

        _resourcePool.ReturnSnapshotContent(_usage, _currentPooledContent);
        _transientResource.ReleaseLease();
        _readOnlySnapshotBundle.Dispose();

        Metrics.ActiveSnapshotBundle--;
    }
}
