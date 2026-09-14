// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

internal sealed class FlatTrieWarmupSession :
    IWorldStateScopeProvider.ITrieWarmupSession,
    ITrieWarmer.IAddressWarmer
{
    private const int DrainWarnMilliseconds = 1000;

    private readonly SnapshotBundle _snapshotBundle;
    private readonly FlatWorldStateScope? _owningScope;
    private readonly ReadOnlySnapshotBundle _readOnlySnapshotBundle;
    private readonly ITrieNodeCache _trieNodeCache;
    private readonly ITrieWarmer _trieWarmer;
    private readonly PatriciaTree _stateTree;
    private readonly ILogManager _logManager;
    private readonly ConcurrentDictionary<AddressAsKey, StorageWarmer?> _storageWarmers = [];
    private volatile int _hintSequenceId;
    private volatile TransientResource _transientResource;
    private bool _isStopped;
    private long _leases = RefCountingLease.Single;
    private long _operations = RefCountingLease.Single;

    public FlatTrieWarmupSession(
        in StateId baseState,
        SnapshotBundle snapshotBundle,
        FlatWorldStateScope? owningScope,
        ReadOnlySnapshotBundle readOnlySnapshotBundle,
        TransientResource transientResource,
        ITrieNodeCache trieNodeCache,
        ITrieWarmer trieWarmer,
        ILogManager logManager)
    {
        _snapshotBundle = snapshotBundle;
        _owningScope = owningScope;
        _readOnlySnapshotBundle = readOnlySnapshotBundle;
        _transientResource = transientResource;
        _trieNodeCache = trieNodeCache;
        _trieWarmer = trieWarmer;
        _logManager = logManager;
        _hintSequenceId = snapshotBundle.HintSequenceId;
        _stateTree = new PatriciaTree(new StateResolver(this), logManager)
        {
            RootHash = baseState.StateRoot.ToCommitment()
        };
    }

    internal void AcquireLease()
    {
        if (!RefCountingLease.TryAcquire(ref _leases)) throw new ObjectDisposedException(nameof(FlatTrieWarmupSession));
    }

    /// <summary>Whether admissions are stopped; a stopped session tears its resources down once drained.</summary>
    internal bool IsStopped => Volatile.Read(ref _isStopped);

    internal void StopWarming()
    {
        if (!Interlocked.Exchange(ref _isStopped, true))
        {
            RefCountingLease.ReleaseOnce(ref _operations);
            DrainOperations();
        }
    }

    /// <summary>
    /// Reopens warming on the same frozen state view with the bundle's current transient resource.
    /// Must run while admissions are stopped and drained, under the bundle's session lock.
    /// </summary>
    /// <remarks>
    /// The session acquires its own lease on the new resource before the retired resource's lease is
    /// released, so the pool cannot reclaim either mid-handover. The retired resource was already handed
    /// to the commit target, so releasing here lets it return to the pool at commit time instead of
    /// pinning it until the session is disposed. The sequence is taken fresh so jobs queued before the
    /// stop stay rejected even after the latch reopens, and the operation counter restarts only after the
    /// previous generation's drain reached its terminal <see cref="RefCountingLease.Disposing"/> state
    /// (not merely zero — <see cref="RefCountingLease.ReleaseOnce"/> CASes 0 → Disposing after the last
    /// release, so a reader that only saw zero could not tell whether teardown had completed).
    /// <see cref="_storageWarmers"/> is deliberately not reset: each contract's storage root stays the
    /// one read from the base state for the whole session, matching the frozen-view design above.
    /// </remarks>
    internal void ResumeWarming(TransientResource transientResource)
    {
        if (!transientResource.TryAcquireLease()) throw new ObjectDisposedException(nameof(FlatTrieWarmupSession));
        TransientResource retired = Interlocked.Exchange(ref _transientResource, transientResource);
        retired.ReleaseLease();
        _hintSequenceId = _snapshotBundle.HintSequenceId;
        _operations = RefCountingLease.Single;
        Volatile.Write(ref _isStopped, false);
    }

    private void DrainOperations()
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        SpinWait spinWait = default;
        while (Volatile.Read(ref _operations) > RefCountingLease.NoAccessors) spinWait.SpinOnce();

        // In-flight warm-up can be several cold RocksDB reads deep; waiting is intentional, this is only a diagnostic.
        ILogger logger = _logManager.GetClassLogger<FlatTrieWarmupSession>();
        if (logger.IsWarn && Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds > DrainWarnMilliseconds)
        {
            logger.Warn($"In-flight trie warm-up operations did not drain within {DrainWarnMilliseconds}ms; still waiting");
        }

        while (Volatile.Read(ref _operations) != RefCountingLease.Disposing) spinWait.SpinOnce();
    }

    public void HintWarmAccount(in ValueAddress address)
    {
        if (!TryEnterOperation(_hintSequenceId)) return;
        try
        {
            // Dedupe first, so the Address allocation below happens at most once per account per block.
            if (!_transientResource.ShouldPrewarm(in address, null)) return;

            // With a BAL present, only accounts it lists as state-changing need their trie path warmed.
            Address accountAddress = address.ToAddress();
            ReadOnlyBlockAccessList? writeSet = _owningScope?.WarmupWriteSet;
            if (writeSet is not null && writeSet.GetAccountChanges(accountAddress)?.HasStateChanges != true) return;

            _trieWarmer.PushAddressJob(this, accountAddress, _hintSequenceId);
        }
        finally
        {
            ExitOperation();
        }
    }

    public void HintWarmSlot(in ValueAddress address, in UInt256 index) =>
        HintWarmSlot(in address, in index, singleProducer: false);

    internal void HintWarmSlot(in ValueAddress address, in UInt256 index, bool singleProducer)
    {
        if (!TryEnterOperation(_hintSequenceId)) return;
        try
        {
            if (!_transientResource.ShouldPrewarm(in address, index)) return;

            Address accountAddress = address.ToAddress();
            StorageWarmer? storageWarmer = _storageWarmers.GetOrAdd(accountAddress, static (address, session) =>
            {
                Hash256 storageRoot = session.GetAccount(address.Value)?.StorageRoot ?? Keccak.EmptyTreeHash;
                return storageRoot == Keccak.EmptyTreeHash
                    ? null
                    : new StorageWarmer(session, address.Value.ToAccountPath.ToHash256(), storageRoot, session._logManager);
            }, this);
            if (storageWarmer is not null
                && (!singleProducer || !_trieWarmer.PushSlotJob(storageWarmer, in index, _hintSequenceId)))
                _trieWarmer.PushSlotJobMpmc(storageWarmer, in index, _hintSequenceId);
        }
        finally
        {
            ExitOperation();
        }
    }

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        if (!TryEnterOperation(sequenceId)) return false;
        try
        {
            _stateTree.WarmUpPath(address.ToAccountPath.Bytes);
            return true;
        }
        finally
        {
            ExitOperation();
        }
    }

    // A job's id is captured from the field itself, so only the bundle's id can diverge from it.
    private bool ShouldStopWarming(int sequenceId) =>
        Volatile.Read(ref _isStopped) || _snapshotBundle.HintSequenceId != sequenceId;

    private bool TryEnterOperation(int sequenceId)
    {
        if (ShouldStopWarming(sequenceId) || !RefCountingLease.TryAcquire(ref _operations)) return false;
        if (!ShouldStopWarming(sequenceId)) return true;

        RefCountingLease.ReleaseOnce(ref _operations);
        return false;
    }

    private void ExitOperation() => RefCountingLease.ReleaseOnce(ref _operations);

    private Account? GetAccount(Address address) =>
        _readOnlySnapshotBundle.GetAccount(address, new HashedKey<Address>(address));

    // State and storage roots are frozen: the session only reads the leased read-only snapshot bundle and its
    // transient resource at the starting root. It never scans the bundle's live _snapshots — any state committed
    // after the session was created already lives there for main processing, so warming it is redundant. Traversal
    // nodes are resolved exclusively through this starting-root view, which is what makes the debug RLP hash
    // check below sound.
    private TrieNode FindStateNodeOrUnknown(in TreePath path, Hash256 hash)
    {
        if (!_readOnlySnapshotBundle.TryFindStateNodes(new HashedKey<TreePath>(path), out TrieNode? node)
            && !_transientResource.TryGetStateNode(in path, hash, out node)
            && !_trieNodeCache.TryGet(address: null, in path, hash, out node))
        {
            node = _transientResource.GetOrAddStateNode(in path, new TrieNode(NodeType.Unknown, hash));
        }

        return ValidateNode(node, address: null, in path, hash);
    }

    private TrieNode FindStorageNodeOrUnknown(Hash256AsKey address, in TreePath path, Hash256 hash)
    {
        if (!_readOnlySnapshotBundle.TryFindStorageNodes(address, path, hash, out TrieNode? node)
            && !_transientResource.TryGetStorageNode(address, in path, hash, out node)
            && !_trieNodeCache.TryGet(address, in path, hash, out node))
        {
            node = _transientResource.GetOrAddStorageNode(address, in path, new TrieNode(NodeType.Unknown, hash));
        }

        return ValidateNode(node, address, in path, hash);
    }

    private static TrieNode ValidateNode(TrieNode node, Hash256? address, in TreePath path, Hash256 hash) =>
        node.Keccak != hash
            ? throw new NodeHashMismatchException($"Node hash mismatch. Address {address}. Path: {path}. Hash: {node.Keccak} vs Requested: {hash}")
            : node;

    private static byte[]? ValidateRlp(byte[]? rlp, Hash256? address, in TreePath path, Hash256 hash)
    {
#if DEBUG
        if (rlp is not null && Keccak.Compute(rlp) != hash)
            throw new NodeHashMismatchException($"Node RLP hash mismatch. Address {address}. Path: {path}. Requested: {hash}");
#endif
        return rlp;
    }

    // Each borrower releases one reference; only the final release cancels and drains active operations.
    // Queued jobs retain no lease, so abandoned jobs cannot keep the warming resources alive.
    public void Dispose()
    {
        if (!RefCountingLease.ReleaseOnce(ref _leases)) return;

        StopWarming();
        try
        {
            _transientResource.ReleaseLease();
        }
        finally
        {
            _readOnlySnapshotBundle.Dispose();
        }
    }

    private sealed class StateResolver(FlatTrieWarmupSession session) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            session.FindStateNodeOrUnknown(in path, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            ValidateRlp(session._readOnlySnapshotBundle.TryLoadStateRlp(in path, hash, flags), address: null, in path, hash);

        public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            address is null ? this : new StorageResolver(session, address);
    }

    private sealed class StorageResolver(FlatTrieWarmupSession session, Hash256AsKey address) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            session.FindStorageNodeOrUnknown(address, in path, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            ValidateRlp(session._readOnlySnapshotBundle.TryLoadStorageRlp(address, in path, hash, flags), address, in path, hash);
    }

    internal sealed class StorageWarmer(
        FlatTrieWarmupSession session,
        Hash256 addressHash,
        Hash256 storageRoot,
        ILogManager logManager) : ITrieWarmer.IStorageWarmer
    {
        private readonly StorageTree _storageTree = new(new StorageResolver(session, addressHash), storageRoot, logManager)
        {
            RootHash = storageRoot
        };

        internal bool IsStopped => session.IsStopped;

        public bool WarmUpStorageTrie(UInt256 index, int sequenceId)
        {
            if (!session.TryEnterOperation(sequenceId)) return false;
            try
            {
                ValueHash256 key = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(index, ref key);
                _storageTree.WarmUpPath(key.BytesAsSpan);
                return true;
            }
            finally
            {
                session.ExitOperation();
            }
        }
    }
}
