using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.Cache;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.Utils;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.TreeStore;

public partial class VerkleTreeStore<TPersistence> : IVerkleTreeStore
    where TPersistence : struct, IPersistenceStrategy
{
    private static Span<byte> RootNodeKey => Array.Empty<byte>();
    private readonly ILogger _logger;

    /// <summary>
    ///     Mapping to a underlying database that keeps tracks of [StateRoot -> BlockNumber] map
    /// </summary>
    private readonly StateRootToBlockMap _stateRootToBlocks;
    public ulong GetBlockNumber(Hash256 rootHash) => (ulong)_stateRootToBlocks[rootHash];

    public VerkleTreeStore(IDbProvider dbProvider, ILogManager logManager)
    {
        _logger = logManager?.GetClassLogger<VerkleTreeStore<TPersistence>>() ?? throw new ArgumentNullException(nameof(logManager));
        Storage = new VerkleKeyValueDb(dbProvider);
        _stateRootToBlocks = new StateRootToBlockMap(dbProvider.StateRootToBlocks);
        if (TPersistence.IsUsingCache)
        {
            BlockCache = new BlockBranchCache(TPersistence.CacheSize);
        }
        BlockCacheSize = TPersistence.CacheSize;
        InitRootHash();
    }

    public VerkleTreeStore(
        IDb leafDb,
        IDb internalDb,
        IDb stateRootToBlocks,
        ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<VerkleTreeStore<TPersistence>>() ?? throw new ArgumentNullException(nameof(logManager));
        Storage = new VerkleKeyValueDb(internalDb, leafDb);
        _stateRootToBlocks = new StateRootToBlockMap(stateRootToBlocks);
        if (TPersistence.IsUsingCache)
        {
            BlockCache = new BlockBranchCache(TPersistence.CacheSize);
        }
        BlockCacheSize = TPersistence.CacheSize;
        InitRootHash();
    }

    // TODO: we can cache this here - but there is just one case - right after the sync completes - the we need to
    //   fetch it from the actual db - not sure how to do that for just one instance
    private Hash256 PersistedStateRoot => GetPersistedStateRoot();
    private long LatestCommittedBlockNumber { get; set; }


    /// <summary>
    ///     Cache used to store state changes for each block - used for serving SnapSync and handling reorgs.
    /// </summary>
    private BlockBranchCache BlockCache { get; }
    private int BlockCacheSize { get; }

    /// <summary>
    ///     The underlying key value database - to persist the final state
    ///     We try to avoid fetching from this, and we only store at the end of a batch insert
    /// </summary>
    private VerkleKeyValueDb Storage { get; }

    /// <summary>
    ///     Keep track of current state root being used for all the operations
    /// </summary>
    public Hash256 StateRoot { get; private set; } = Hash256.Zero;

    public IReadOnlyVerkleTreeStore AsReadOnly(VerkleMemoryDb tempKeyValueStore)
    {
        return new ReadOnlyVerkleStateStore(this, tempKeyValueStore);
    }

    public byte[]? GetLeaf(ReadOnlySpan<byte> key, Hash256? stateRoot = null)
    {
        Hash256 stateRootToUse = stateRoot ?? StateRoot;
        byte[] value;
        if (TPersistence.IsUsingCache)
        {
            value = BlockCache.GetLeaf(key.ToArray(), stateRootToUse);
            if (value is not null) return value;
        }
        return Storage.GetLeaf(key, out value) ? value : null;
    }

    public InternalNode? GetInternalNode(ReadOnlySpan<byte> key, Hash256? stateRoot = null)
    {
        Hash256 stateRootToUse = stateRoot ?? StateRoot;
        InternalNode? value;
        if (TPersistence.IsUsingCache)
        {
            value = BlockCache.GetInternalNode(key.ToArray(), stateRootToUse);
            if (value is not null) return value;
        }
        return Storage.GetInternalNode(key, out value) ? value : null;
    }

    public bool HasStateForBlock(Hash256 stateRoot)
    {
        Hash256 stateRootToCheck = stateRoot;

        // just a edge case - to account for empty MPT state root
        if (stateRoot.Equals(Keccak.EmptyTreeHash)) stateRootToCheck = Hash256.Zero;
        if (stateRootToCheck == Hash256.Zero) return true;

        // check in cache and then check for the persisted state root
        if (TPersistence.IsUsingCache)
        {
            if (BlockCache.GetStateRootNode(stateRootToCheck, out _)) return true;
        }
        // _logger.Info($"HasStateForBlock - PersistedStateRoot : {PersistedStateRoot} {stateRootToCheck}");
        return PersistedStateRoot == stateRootToCheck;
    }

    public bool MoveToStateRoot(Hash256 stateRoot)
    {
        // this is mostly needed for tests
        if (stateRoot == Hash256.Zero)
        {
            StateRoot = Hash256.Zero;
            return true;
        }

        // when using memory cache, check for node in BlockCache first
        if (TPersistence.IsUsingCache)
        {
            if (BlockCache.GetStateRootNode(stateRoot, out BlockBranchNode? rootNode))
            {
                StateRoot = rootNode.Data.StateRoot;
                return true;
            }
        }

        if (stateRoot != PersistedStateRoot) return false;
        StateRoot = stateRoot;

        return true;
    }


    /// <summary>
    ///     Events that are used by the VerkleArchiveStore to build the archive index
    /// </summary>
    public event EventHandler<InsertBatchCompletedV1>? InsertBatchCompletedV1;

    public event EventHandler<InsertBatchCompletedV2>? InsertBatchCompletedV2;

    private void InitRootHash()
    {
        if (Storage.GetInternalNode(RootNodeKey, out InternalNode rootNode))
        {
            // PersistedStateRoot = StateRoot = new Hash256(rootNode!.InternalCommitment.ToBytes());
            LastPersistedBlockNumber = _stateRootToBlocks[StateRoot];
            if (LastPersistedBlockNumber == -2) throw new Exception("StateRoot To BlockNumber Cache Corrupted");
            LatestCommittedBlockNumber = -1;
        }
        else
        {
            Storage.SetInternalNode(RootNodeKey, new InternalNode(VerkleNodeType.BranchNode));
            // PersistedStateRoot = StateRoot = Hash256.Zero;
            LastPersistedBlockNumber = LatestCommittedBlockNumber = -1;
        }
    }

    private Hash256 GetPersistedStateRoot()
    {
        Hash256 rootHash = Hash256.Zero;
        if (Storage.GetInternalNode(RootNodeKey, out InternalNode rootNode))
            rootHash = new Hash256(rootNode!.InternalCommitment.ToBytes());
        return rootHash;
    }

    private static Hash256? GetStateRoot(InternalStore db)
    {
        return db.TryGetValue(RootNodeKey, out InternalNode? node) ? node!.Bytes : null;
    }

    /// <summary>
    ///     Keep track of LastPersistedBlock and if the LastPersistedBlock reached the reorg boundary and was marked safe
    /// </summary>
    private bool _lastPersistedReachedReorgBoundary;

    private long _latestPersistedBlockNumber;
    private long LastPersistedBlockNumber
    {
        get => _latestPersistedBlockNumber;
        set
        {
            if (value != _latestPersistedBlockNumber)
            {
                _latestPersistedBlockNumber = value;
                _lastPersistedReachedReorgBoundary = false;
            }
        }
    }
    private int _isFirst;
    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
    private void AnnounceReorgBoundaries()
    {
        if (LatestCommittedBlockNumber < 1) return;

        var shouldAnnounceReorgBoundary = false;
        var isFirstCommit = Interlocked.Exchange(ref _isFirst, 1) == 0;
        if (isFirstCommit)
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"Reached first commit - newest {LatestCommittedBlockNumber}, last persisted {LastPersistedBlockNumber}");
            // this is important when transitioning from fast sync
            // imagine that we transition at block 1200000
            // and then we close the app at 1200010
            // in such case we would try to continue at Head - 1200010
            // because head is loaded if there is no persistence checkpoint
            // so we need to force the persistence checkpoint
            var baseBlock = Math.Max(0, LatestCommittedBlockNumber - 1);
            LastPersistedBlockNumber = baseBlock;
            shouldAnnounceReorgBoundary = true;
        }
        else if (!_lastPersistedReachedReorgBoundary)
        {
            // even after we persist a block we do not really remember it as a safe checkpoint
            // until max reorgs blocks after
            if (LatestCommittedBlockNumber >= LastPersistedBlockNumber + BlockCacheSize)
                shouldAnnounceReorgBoundary = true;
        }

        if (shouldAnnounceReorgBoundary)
        {
            ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(LastPersistedBlockNumber));
            _lastPersistedReachedReorgBoundary = true;
        }
    }
}
