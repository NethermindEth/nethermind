// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Rust.Fork;

/// <summary>
/// A mutable bundle wrapping a <see cref="ReadOnlySnapshotBundle"/> with a write buffer in a native scope.
/// </summary>
/// <remarks>
/// The native scope holds the current content and the snapshots this bundle committed or was seeded with, trie nodes
/// as RLP. C# keeps the trie node objects only in the transient resource, the trie node cache and the
/// <see cref="NodeCaptures"/> of the <c>IsPersisted</c> replay.
/// </remarks>
public sealed class SnapshotBundle : IDisposable
{
    private readonly ReadOnlySnapshotBundle _readOnlySnapshotBundle;

    /// <summary>
    /// Passthrough of <see cref="ReadOnlySnapshotBundle.IsHistorical"/>: the wrapped bundle is history-backed and
    /// trie-less, so the scope must skip post-block state-root trie recomputation.
    /// </summary>
    public bool IsHistorical => _readOnlySnapshotBundle.IsHistorical;

    // A full branch node, the largest node of an Ethereum trie; a batch of node writes reserves this much RLP per node.
    private const int BranchNodeRlpSize = 532;
    private const int MaxBatchRlpSize = 1024 * 1024;

    private static readonly byte[] ZeroHash = new byte[Hash256.Size];

    // The bundle whose read-only bundle this thread leased for a warmer traversal; see TryLeaseReadOnlyBundle.
    [ThreadStatic] private static SnapshotBundle? t_warmerLeaseOwner;
    [ThreadStatic] private static int t_warmerLeaseDepth;

    private readonly ScopeHandle _scope;
    // The leases of the overridable chain the scope was seeded with, held until Dispose as C# holds them.
    private readonly SnapshotPooledList? _ownSeed;
    // False only while no snapshot below or of this bundle clears a storage, so no self-destruct index needs a call.
    private volatile bool _mayHaveSelfDestructs;
    private NodeCaptures? _captures;
    // The nodes of commits that returned no snapshot, pruned at Dispose as returning the C# content prunes them.
    private ArrayPoolList<CapturedNodes>? _heldCaptures;

    // The cached resource holds some items that are pooled.
    // Notably, it holds loaded caches from trie warmer.
    private TransientResource _transientResource = null!;

    private readonly ITrieNodeCache _trieNodeCache;
    private bool _isDisposed;
    private readonly IResourcePool _resourcePool;

    internal ResourcePool.Usage _usage;

    /// <param name="filterInMemorySlotReads">Serve slot reads with
    /// <see cref="ReadOnlySnapshotBundle.GetSlotFiltered"/>; for read-only execution only.</param>
    public SnapshotBundle(
        ReadOnlySnapshotBundle readOnlySnapshotBundle,
        ITrieNodeCache trieNodeCache,
        IResourcePool resourcePool,
        ResourcePool.Usage usage,
        SnapshotPooledList? snapshots = null,
        bool filterInMemorySlotReads = false)
    {
        _readOnlySnapshotBundle = readOnlySnapshotBundle;
        _scope = readOnlySnapshotBundle.TakeOrCreateScope(snapshots, filterInMemorySlotReads, usage, out bool mayHaveSelfDestructs);
        _ownSeed = snapshots;
        _mayHaveSelfDestructs = mayHaveSelfDestructs;
        _trieNodeCache = trieNodeCache;
        _resourcePool = resourcePool;
        _usage = usage;

        try
        {
            _transientResource = resourcePool.GetCachedResource(usage);
        }
        catch
        {
            // The caller still owns the read-only bundle.
            _scope.Dispose();
            throw;
        }

        Metrics.ActiveSnapshotBundle++;
    }

    public Account? GetAccount(Address address)
    {
        GuardDispose();

        return _readOnlySnapshotBundle.ReadScopeAccount(_scope, promote: false, address);
    }

    // The native read caches an account read below the current content in it, so the caller's PromoteAccount is skipped.
    internal Account? GetAccount(Address address, out bool isInCurrentSnapshot)
    {
        GuardDispose();

        isInCurrentSnapshot = true;
        return _readOnlySnapshotBundle.ReadScopeAccount(_scope, promote: true, address);
    }

    public unsafe int DetermineSelfDestructSnapshotIdx(Address address)
    {
        if (!_mayHaveSelfDestructs) return -1;

        int index;
        using BundleBaseHandle.Call call = _scope.Enter();
        fixed (byte* addressBytes = address.Bytes)
        {
            ThrowIfFailed(NativeMethods.fdb_scope_self_destruct_idx(call.Bundle, addressBytes, &index));
        }

        return index;
    }

    public void GetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out UInt256? value)
    {
        GuardDispose();

        value = _readOnlySnapshotBundle.ReadScopeSlot(_scope, address, index, selfDestructStateIdx);
    }

    public TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash)
    {
        GuardDispose();

        if (_transientResource.TryGetStateNode(path, hash, out TrieNode? node) && node.NodeType != NodeType.Unknown)
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
        // The warmer never reads the recyclable _snapshots; it warms nodes from persistence into the
        // _transientResource. Pin the transient per read (lease + ABA re-check) while the bundle is live,
        // else fall back to a persistence-only read (the bundle is being torn down).
        TransientResource? transientResource = TryLeaseTransientResource();
        if (transientResource is null)
        {
            return TryFindStateNodeInPersistence(path, hash, out TrieNode? node) ? node : CreateWarmerUnknownNode(hash);
        }

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
            return node;
        }

        return TryFindStateNodeInPersistence(path, hash, out node)
            ? transientResource.GetOrAddStateNode(path, node)
            : transientResource.GetOrAddStateNode(path, CreateWarmerUnknownNode(hash));
    }

    private static TrieNode CreateWarmerUnknownNode(Hash256 hash)
    {
        TrieNode node = new(NodeType.Unknown, hash);
        ForkInternals.MarkWarmerOwned(node);
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

    private bool TryFindStateNodeInPersistence(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        if (_trieNodeCache.TryGet(null, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
            return true;
        }

        HashedKey<TreePath> key = new(path);
        return _readOnlySnapshotBundle.TryFindStateNodes(key, out node);
    }

    private bool DoFindStateNodeExternal(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        if (_trieNodeCache.TryGet(null, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
            return true;
        }

        HashedKey<TreePath> key = new(path);
        return _readOnlySnapshotBundle.TryFindStateNodes(key, out node);
    }

    public TrieNode FindStorageNodeOrUnknown(Hash256 address, in TreePath path, Hash256 hash)
    {
        GuardDispose();

        if (_transientResource.TryGetStorageNode((Hash256AsKey)address, path, hash, out TrieNode? node)
            && node.NodeType != NodeType.Unknown)
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


    public TrieNode FindStorageNodeOrUnknownTrieWarmer(Hash256 address, in TreePath path, Hash256 hash)
    {
        // Persistence-only external find, same reasoning as FindStateNodeOrUnknownForTrieWarmer.
        // Pin the transient per read (lease + ABA re-check) while the bundle is live, else fall back
        // to a persistence-only read (the bundle is being torn down).
        TransientResource? transientResource = TryLeaseTransientResource();
        if (transientResource is null)
        {
            return TryFindStorageNodeInPersistence(address, path, hash, out TrieNode? node)
                ? node
                : CreateWarmerUnknownNode(hash);
        }

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
            return node;
        }

        return TryFindStorageNodeInPersistence(address, path, hash, out node)
            ? transientResource.GetOrAddStorageNode((Hash256AsKey)address, path, node)
            : transientResource.GetOrAddStorageNode((Hash256AsKey)address, path, CreateWarmerUnknownNode(hash));
    }

    private bool TryFindStorageNodeInPersistence(Hash256 address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        if (_trieNodeCache.TryGet(address, path, hash, out node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
            return true;
        }

        HashedKey<(Hash256, TreePath)> key = new((address, path));
        return _readOnlySnapshotBundle.TryFindStorageNodes(key, out node);
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
        return _readOnlySnapshotBundle.TryFindStorageNodes(key, out node);
    }

    public byte[]? TryLoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        return IsWarmerRead
            ? _readOnlySnapshotBundle.TryLoadStateRlp(path, hash, flags)
            : _readOnlySnapshotBundle.LoadScopeRlp(_scope, null, path, hash, flags);
    }

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        return IsWarmerRead
            ? _readOnlySnapshotBundle.TryLoadStorageRlp(address, path, hash, flags)
            : _readOnlySnapshotBundle.LoadScopeRlp(_scope, address, path, hash, flags);
    }

    // This is called only during trie commit
    public void SetStateNode(in TreePath path, TrieNode newNode)
    {
        GuardDispose();
        if (!newNode.IsSealed) throw new Exception("Node must be sealed for setting");

        // Note: Hot path
        WriteNode(null, path, newNode);
        _transientResource.UpdateStateNode(path, newNode);
    }

    internal void PublishStateNodes(IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers)
    {
        GuardDispose();
        PutNodes(null, buffers);
    }

    internal AddressStorageNodeDictionary.AddressNodes GetStorageNodeDestination(Hash256 address) => new(address);

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
        WriteNode(nodes.AddressHash, path, newNode);
        _transientResource.UpdateStorageNode(addr, path, newNode);
    }

    internal void PublishStorageNodes(
        AddressStorageNodeDictionary.AddressNodes nodes,
        Hash256 address,
        IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers)
    {
        GuardDispose();
        PutNodes(nodes.AddressHash, buffers);
    }

    private static int CountBufferedNodes(IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers)
    {
        int count = 0;
        foreach (List<(TreePath Path, TrieNode Node)> buffer in buffers) count += buffer.Count;
        return count;
    }

    /// <summary>Writes a node into the native scope: of the state trie, or of the storage trie of <paramref name="addressHash"/>.</summary>
    private unsafe void WriteNode(Hash256? addressHash, in TreePath path, TrieNode node)
    {
        ReadOnlySpan<byte> rlp = RlpOf(node);
        using (BundleBaseHandle.Call call = _scope.Enter())
        {
            fixed (byte* pathBytes = path.Path.Bytes, hash = HashOf(node), rlpBytes = rlp)
            {
                if (addressHash is null)
                {
                    ThrowIfFailed(NativeMethods.fdb_scope_set_state_node(call.Bundle, pathBytes, (byte)path.Length, hash, rlpBytes, (uint)rlp.Length));
                }
                else
                {
                    fixed (byte* addressBytes = addressHash.Bytes)
                    {
                        ThrowIfFailed(NativeMethods.fdb_scope_set_storage_node(call.Bundle, addressBytes, pathBytes, (byte)path.Length, hash, rlpBytes, (uint)rlp.Length));
                    }
                }
            }
        }

        Capture(addressHash, path, node);
    }

    /// <summary>
    /// <see cref="WriteNode"/> for every buffered node, in native batches of at most <see cref="MaxBatchRlpSize"/>
    /// bytes of RLP; a committer publishes its buffers once, on one thread.
    /// </summary>
    private void PutNodes(Hash256? addressHash, IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers)
    {
        int count = CountBufferedNodes(buffers);
        if (count == 0) return;

        FfiNodeRecord[] records = ArrayPool<FfiNodeRecord>.Shared.Rent(count);
        (TreePath Path, TrieNode Node)[] nodes = ArrayPool<(TreePath Path, TrieNode Node)>.Shared.Rent(count);
        byte[] blob = ArrayPool<byte>.Shared.Rent(Math.Min(count * BranchNodeRlpSize, MaxBatchRlpSize));
        int nodeCount = 0;
        int blobLength = 0;
        try
        {
            foreach (List<(TreePath Path, TrieNode Node)> buffer in buffers)
            {
                foreach ((TreePath path, TrieNode node) in buffer)
                {
                    if (!node.IsSealed) throw new Exception("Node must be sealed for setting");

                    ReadOnlySpan<byte> rlp = RlpOf(node);
                    if (blobLength + rlp.Length > blob.Length)
                    {
                        PutBatch(addressHash, records.AsSpan(0, nodeCount), nodes.AsSpan(0, nodeCount), blob.AsSpan(0, blobLength));
                        nodeCount = 0;
                        blobLength = 0;
                        if (rlp.Length > blob.Length)
                        {
                            ArrayPool<byte>.Shared.Return(blob);
                            blob = ArrayPool<byte>.Shared.Rent(rlp.Length);
                        }
                    }

                    records[nodeCount] = new FfiNodeRecord(path, node.Keccak, rlp.Length, blobLength);
                    nodes[nodeCount++] = (path, node);
                    rlp.CopyTo(blob.AsSpan(blobLength));
                    blobLength += rlp.Length;
                }
            }

            PutBatch(addressHash, records.AsSpan(0, nodeCount), nodes.AsSpan(0, nodeCount), blob.AsSpan(0, blobLength));
        }
        finally
        {
            ArrayPool<FfiNodeRecord>.Shared.Return(records);
            ArrayPool<(TreePath Path, TrieNode Node)>.Shared.Return(nodes, clearArray: true);
            ArrayPool<byte>.Shared.Return(blob);
        }
    }

    /// <summary>Writes one native batch, then keeps its <paramref name="nodes"/> as <see cref="WriteNode"/> does.</summary>
    private unsafe void PutBatch(Hash256? addressHash, ReadOnlySpan<FfiNodeRecord> records, ReadOnlySpan<(TreePath Path, TrieNode Node)> nodes, ReadOnlySpan<byte> blob)
    {
        if (records.IsEmpty) return;

        using (BundleBaseHandle.Call call = _scope.Enter())
        {
            fixed (FfiNodeRecord* recordPointer = records)
            fixed (byte* blobBytes = blob)
            {
                if (addressHash is null)
                {
                    ThrowIfFailed(NativeMethods.fdb_scope_put_state_nodes(call.Bundle, recordPointer, (uint)records.Length, blobBytes, (nuint)blob.Length));
                }
                else
                {
                    fixed (byte* addressBytes = addressHash.Bytes)
                    {
                        ThrowIfFailed(NativeMethods.fdb_scope_put_storage_nodes(call.Bundle, addressBytes, recordPointer, (uint)records.Length, blobBytes, (nuint)blob.Length));
                    }
                }
            }
        }

        foreach ((TreePath path, TrieNode node) in nodes)
        {
            Capture(addressHash, path, node);
            if (addressHash is null) _transientResource.UpdateStateNode(path, node);
            else _transientResource.UpdateStorageNode(addressHash, path, node);
        }
    }

    /// <summary>Keeps a node for the <c>IsPersisted</c> replay; a hash-only placeholder is never persisted, so it is not kept.</summary>
    private void Capture(Hash256? addressHash, in TreePath path, TrieNode node)
    {
        if (node.IsHashOnlyPlaceholder()) return;

        NodeCaptures captures = Volatile.Read(ref _captures) ?? CreateCaptures();
        if (addressHash is null) captures.AddState(path, node);
        else captures.AddStorage(addressHash, path, node);
    }

    private NodeCaptures CreateCaptures()
    {
        NodeCaptures created = new();
        return Interlocked.CompareExchange(ref _captures, created, null) ?? created;
    }

    /// <summary>An inline node has no hash and is written with a zero one.</summary>
    private static ReadOnlySpan<byte> HashOf(TrieNode node) => node.Keccak is { } keccak ? keccak.Bytes : ZeroHash;

    private static ReadOnlySpan<byte> RlpOf(TrieNode node) =>
        node.IsHashOnlyPlaceholder() ? ReadOnlySpan<byte>.Empty : node.FullRlp.AsSpan();

    public void SetAccount(Address address, Account? account) => WriteAccount(address, account, promote: false);

    internal void PromoteAccount(Address address, Account? account) => WriteAccount(address, account, promote: true);

    /// <summary>Sets <paramref name="account"/>, null for a deleted one, or with <paramref name="promote"/> caches it unless the current content holds the address.</summary>
    private unsafe void WriteAccount(Address address, Account? account, bool promote)
    {
        FfiAccount nativeAccount = account is null ? default : new FfiAccount(account);
        FfiAccount* accountPointer = account is null ? null : &nativeAccount;
        using BundleBaseHandle.Call call = _scope.Enter();
        fixed (byte* addressBytes = address.Bytes)
        {
            ThrowIfFailed(promote
                ? NativeMethods.fdb_scope_promote_account(call.Bundle, addressBytes, accountPointer)
                : NativeMethods.fdb_scope_set_account(call.Bundle, addressBytes, accountPointer));
        }
    }

    public unsafe void SetChangedSlot(Address address, in UInt256 index, in UInt256 value)
    {
        // A zero value is a deletion, which the native content records as one.
        ulong* slot = stackalloc ulong[4] { index.u0, index.u1, index.u2, index.u3 };
        ulong* slotValue = stackalloc ulong[4] { value.u0, value.u1, value.u2, value.u3 };
        using BundleBaseHandle.Call call = _scope.Enter();
        fixed (byte* addressBytes = address.Bytes)
        {
            ThrowIfFailed(NativeMethods.fdb_scope_set_slot(call.Bundle, addressBytes, slot, slotValue));
        }
    }

    internal unsafe void ClearStorage(Address address, Hash256 addressHash)
    {
        GuardDispose();

        _mayHaveSelfDestructs = true;
        using (BundleBaseHandle.Call call = _scope.Enter())
        {
            fixed (byte* addressBytes = address.Bytes, addressHashBytes = addressHash.Bytes)
            {
                ThrowIfFailed(NativeMethods.fdb_scope_clear_storage(call.Bundle, addressBytes, addressHashBytes));
            }
        }

        _captures?.RemoveAddress(addressHash);
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
    /// While this thread holds the lease, its node loads read only the read-only bundle, never the scope's current
    /// content or own snapshots, as the C# warmers read only the in-memory snapshots below and the persistence.
    /// </remarks>
    /// <returns><c>false</c> when the bundle is already fully disposed; the caller must skip the traversal.</returns>
    internal bool TryLeaseReadOnlyBundle()
    {
        if (!_readOnlySnapshotBundle.TryLease()) return false;

        Debug.Assert(t_warmerLeaseOwner is null || ReferenceEquals(t_warmerLeaseOwner, this));
        t_warmerLeaseOwner = this;
        t_warmerLeaseDepth++;
        return true;
    }

    /// <summary>Releases a lease taken with <see cref="TryLeaseReadOnlyBundle"/>.</summary>
    internal void ReleaseReadOnlyBundleLease()
    {
        if (--t_warmerLeaseDepth == 0) t_warmerLeaseOwner = null;
        _readOnlySnapshotBundle.Dispose();
    }

    private bool IsWarmerRead => ReferenceEquals(t_warmerLeaseOwner, this);

    public (Snapshot?, TransientResource?) CollectAndApplySnapshot(StateId from, StateId to, bool returnSnapshot = true)
    {
        // Writers have joined, so this commit's captures are complete. The native scope keeps the committed snapshot
        // whether it is returned or not, so later reads are correct.
        SnapshotHandle? committed = ScopeCommit(from, to, returnSnapshot);
        CapturedNodes? nodes = _captures?.Take();

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

            return (new Snapshot(from, to, committed!, nodes), transientResource);
        }
        else
        {
            if (nodes is not null) (_heldCaptures ??= new ArrayPoolList<CapturedNodes>(1)).Add(nodes);

            TransientResource retired = _transientResource;
            SwapTransientResource();
            retired.ReleaseLease();

            return (null, null);
        }
    }

    /// <summary>Freezes the current content into the scope's snapshot from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <returns>The snapshot when <paramref name="keep"/>, else null.</returns>
    private unsafe SnapshotHandle? ScopeCommit(in StateId from, in StateId to, bool keep)
    {
        FfiStateId nativeFrom = new(from);
        FfiStateId nativeTo = new(to);
        nint snapshot;
        using (BundleBaseHandle.Call call = _scope.Enter())
        {
            ThrowIfFailed(NativeMethods.fdb_scope_commit(call.Bundle, &nativeFrom, &nativeTo, keep ? (byte)1 : (byte)0, &snapshot));
        }

        return snapshot == 0 ? null : new SnapshotHandle(snapshot);
    }

    private void SwapTransientResource() =>
        Volatile.Write(ref _transientResource, _resourcePool.GetCachedResource(_usage));

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);

    private void ThrowIfFailed(FdbStatus status) => _scope.Store.ThrowIfFailed(status);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true)) return;

        // No field is nulled: a late call from a trie warmer throws ObjectDisposedException from the closed scope.
        _scope.Dispose();
        _captures?.PruneAndDispose();
        if (_heldCaptures is not null)
        {
            foreach (CapturedNodes held in _heldCaptures) held.PruneAndDispose();
            _heldCaptures.Dispose();
        }

        _ownSeed?.Dispose();
        _transientResource.ReleaseLease();
        _readOnlySnapshotBundle.Dispose();

        Metrics.ActiveSnapshotBundle--;
    }
}
