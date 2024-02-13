// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Serializers;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeStore;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Synchronization.VerkleSync;

public class VerkleSyncProvider : IVerkleSyncProvider
{
    private readonly ObjectPool<IVerkleTreeStore> _trieStorePool;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    private readonly VerkleProgressTracker _progressTracker;

    public VerkleSyncProvider(VerkleProgressTracker progressTracker, IDbProvider dbProvider, ILogManager logManager)
    {
        IDbProvider dbProvider1 = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
        _progressTracker = progressTracker ?? throw new ArgumentNullException(nameof(progressTracker));
        _trieStorePool = new DefaultObjectPool<IVerkleTreeStore>(new TrieStorePoolPolicy(dbProvider1, logManager));

        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = logManager.GetClassLogger();
    }

    public bool CanSync() => _progressTracker.CanSync();

    public AddRangeResult AddSubTreeRange(SubTreeRange request, SubTreesAndProofs response)
    {
        AddRangeResult result;

        if (response.SubTrees.Length == 0 && response.Proofs.Length == 0)
        {
            if (_logger.IsTrace) _logger.Trace($"VERKLE_SYNC - GetSubTreeRange - requested expired RootHash:{request.RootHash}");

            result = AddRangeResult.ExpiredRootHash;
        }
        else
        {
            result = AddSubTreeRange(request.BlockNumber.Value, request.RootHash, request.StartingStem, response.SubTrees, response.Proofs, limitStem: request.LimitStem);

            if (result == AddRangeResult.OK)
            {
                Interlocked.Add(ref Metrics.SnapSyncedAccounts, response.SubTrees.Length);
            }
        }

        _progressTracker.ReportSubTreeRangePartitionFinished(request.LimitStem);

        return result;
    }

    public AddRangeResult AddSubTreeRange(long blockNumber, Hash256 expectedRootHash, Stem startingStem,
        PathWithSubTree[] subTrees, byte[]? proofs = null, Stem? limitStem = null)
    {
        limitStem ??= Keccak.MaxValue.Bytes[..31].ToArray();
        Banderwagon rootPoint = Banderwagon.FromBytes(expectedRootHash.Bytes.ToArray()) ?? throw new Exception("root point invalid");
        IVerkleTreeStore store = _trieStorePool.Get();
        try
        {
            VerkleProofSerializer ser = VerkleProofSerializer.Instance;
            VerkleProof vProof = ser.Decode(new RlpStream(proofs!));
            try
            {
                var stateStore = new VerkleTreeStore<PersistEveryBlock>(new MemDb(), new MemDb(), new MemDb(), _logManager);
                var localTree = new VerkleTree(stateStore, LimboLogs.Instance);
                var isCorrect = localTree.CreateStatelessTreeFromRange(vProof, rootPoint, startingStem, subTrees[^1].Path, subTrees);
                if (!isCorrect)
                {
                    _logger.Error(
                        $"VERKLE_SYNC - AddSubTreeRange failed, expected {blockNumber}:{expectedRootHash}, startingHash:{startingStem} {isCorrect}");
                    return AddRangeResult.DifferentRootHash;
                }
                store.InsertSyncBatch(0, localTree._treeCache);
                _logger.Info($"VERKLE_SYNC - AddSubTreeRange SUCCESS, expected {blockNumber}:{expectedRootHash}, startingHash:{startingStem} endingHash:{subTrees[^1].Path}");
            }
            catch (Exception e)
            {
                _logger.Error($"AddSubTreeRange: {blockNumber} {expectedRootHash} {startingStem}");
                _logger.Error("something broke during sync", e);
                throw;
            }

            _progressTracker.UpdateSubTreePartitionProgress(limitStem, subTrees[^1].Path,
                startingStem == subTrees[^1].Path);
            return AddRangeResult.OK;
        }
        finally
        {
            _trieStorePool.Return(store);
        }
    }


    public AddRangeResult AddSubTreeRange(long blockNumber, Banderwagon rootPoint, byte[] startingStem,
        PathWithSubTree[] subTrees, VerkleProof proof, byte[] limitStem)
    {
        IVerkleTreeStore store = _trieStorePool.Get();
        VerkleTree tree = new VerkleTree(store, LimboLogs.Instance);
        bool correct =
            tree.CreateStatelessTreeFromRange(proof, rootPoint, startingStem, limitStem,
                subTrees);
        if (!correct) return AddRangeResult.DifferentRootHash;
        return AddRangeResult.OK;
    }

    public bool HealTheTreeFromExecutionWitness(ExecutionWitness execWitness, Banderwagon root)
    {
        IVerkleTreeStore store = _trieStorePool.Get();
        VerkleTree tree = new(store, LimboLogs.Instance);
        return tree.InsertIntoStatelessTree(execWitness, root, false);
    }

    public void RefreshLeafs(LeafToRefreshRequest request, byte[][] response)
    {
        throw new NotImplementedException();
    }

    private void RetryLeafRefresh(byte[] leaf)
    {
        _progressTracker.EnqueueLeafRefresh(leaf);
    }

    public void RetryRequest(VerkleSyncBatch batch)
    {
        if (batch.SubTreeRangeRequest is not null)
        {
            _progressTracker.ReportSubTreeRangePartitionFinished(batch.SubTreeRangeRequest.LimitStem);
        }
        else if (batch.LeafToRefreshRequest is not null)
        {
            _progressTracker.ReportLeafRefreshFinished(batch.LeafToRefreshRequest);
        }
    }

    public bool IsVerkleGetRangesFinished() => _progressTracker.IsGetRangesFinished();

    public void UpdatePivot()
    {
        _progressTracker.UpdatePivot();
    }

    public (VerkleSyncBatch request, bool finished) GetNextRequest() => _progressTracker.GetNextRequest();

    private class TrieStorePoolPolicy : IPooledObjectPolicy<IVerkleTreeStore>
    {
        private readonly IDbProvider _dbProvider;
        private readonly ILogManager _logManager;

        public TrieStorePoolPolicy(IDbProvider provider, ILogManager logManager)
        {
            _dbProvider = provider;
            _logManager = logManager;
        }

        public IVerkleTreeStore Create()
        {
            return new VerkleTreeStore<PersistEveryBlock>(_dbProvider, _logManager);
        }

        public bool Return(IVerkleTreeStore obj)
        {
            return true;
        }
    }
}
