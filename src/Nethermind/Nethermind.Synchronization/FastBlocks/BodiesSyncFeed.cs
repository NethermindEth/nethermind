// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class BodiesSyncFeed : ActivatedSyncFeed<BodiesSyncBatch?>
    {
        private int _requestSize = GethSyncLimits.MaxBodyFetch;

        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly ISpecProvider _specProvider;
        private readonly ISyncPeerPool _syncPeerPool;

        private long _pivotNumber;
        private readonly long _barrier;

        private SyncStatusList _syncStatusList;

        public BodiesSyncFeed(
            ISyncModeSelector syncModeSelector,
            IBlockTree blockTree,
            ISyncPeerPool syncPeerPool,
            ISyncConfig syncConfig,
            ISyncReport syncReport,
            ISpecProvider specProvider,
            ILogManager logManager) : base(syncModeSelector)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));

            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException(
                    "Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = _syncConfig.PivotNumberParsed;
            _barrier = _barrier = _syncConfig.AncientBodiesBarrierCalc;
            if (_logger.IsInfo) _logger.Info($"Using pivot {_pivotNumber} and barrier {_barrier} in bodies sync");

            ResetSyncStatusList();
        }

        public override void InitializeFeed()
        {
            if (_pivotNumber < _syncConfig.PivotNumberParsed)
            {
                _pivotNumber = _syncConfig.PivotNumberParsed;
                if (_logger.IsInfo) _logger.Info($"Changed pivot in bodies sync. Now using pivot {_pivotNumber} and barrier {_barrier}");
                ResetSyncStatusList();
            }

            base.InitializeFeed();
        }

        private void ResetSyncStatusList()
        {
            _syncStatusList = new SyncStatusList(
                _blockTree,
                _pivotNumber,
                _blockTree.LowestInsertedBodyNumber);
        }

        protected override SyncMode ActivationSyncModes { get; } = SyncMode.FastBodies & ~SyncMode.FastBlocks;

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.Bodies;

        private bool ShouldBuildANewBatch()
        {
            bool shouldDownloadBodies = _syncConfig.DownloadBodiesInFastSync;
            bool allBodiesDownloaded = _syncStatusList.LowestInsertWithoutGaps <= _barrier;
            bool shouldFinish = !shouldDownloadBodies || allBodiesDownloaded;
            if (shouldFinish)
            {
                ResetSyncStatusList();
                Finish();
                PostFinishCleanUp();

                return false;
            }

            return true;
        }

        private void PostFinishCleanUp()
        {
            _syncReport.FastBlocksBodies.Update(_pivotNumber);
            _syncReport.FastBlocksBodies.MarkEnd();
            _syncReport.BodiesInQueue.Update(0);
            _syncReport.BodiesInQueue.MarkEnd();
        }

        public override Task<BodiesSyncBatch?> PrepareRequest(CancellationToken token = default)
        {
            BodiesSyncBatch? batch = null;
            if (ShouldBuildANewBatch())
            {
                BlockInfo?[] infos = new BlockInfo[_requestSize];
                _syncStatusList.GetInfosForBatch(infos);
                if (infos[0] is not null)
                {
                    batch = new BodiesSyncBatch(infos);
                    batch.MinNumber = infos[0].BlockNumber;
                    batch.Prioritized = true;
                }
            }

            _blockTree.LowestInsertedBodyNumber = _syncStatusList.LowestInsertWithoutGaps;

            return Task.FromResult(batch);
        }

        public override SyncResponseHandlingResult HandleResponse(BodiesSyncBatch? batch, PeerInfo peer = null)
        {
            batch?.MarkHandlingStart();
            try
            {
                if (batch is null)
                {
                    if (_logger.IsDebug) _logger.Debug("Received a NULL batch as a response");
                    return SyncResponseHandlingResult.InternalError;
                }

                int added = InsertBodies(batch);
                return added == 0
                    ? SyncResponseHandlingResult.NoProgress
                    : SyncResponseHandlingResult.OK;
            }
            finally
            {
                batch?.MarkHandlingEnd();
            }
        }

        private bool TryPrepareBlock(BlockInfo blockInfo, BlockBody blockBody, out Block? block)
        {
            BlockHeader header = _blockTree.FindHeader(blockInfo.BlockHash);
            bool txRootIsValid = new TxTrie(blockBody.Transactions).RootHash == header.TxRoot;
            bool unclesHashIsValid = UnclesHash.Calculate(blockBody.Uncles) == header.UnclesHash;
            if (txRootIsValid && unclesHashIsValid)
            {
                block = new Block(header, blockBody);
            }
            else
            {
                block = null;
            }

            return block is not null;
        }

        private int InsertBodies(BodiesSyncBatch batch)
        {
            bool hasBreachedProtocol = false;
            int validResponsesCount = 0;

            for (int i = 0; i < batch.Infos.Length; i++)
            {
                BlockInfo? blockInfo = batch.Infos[i];
                BlockBody? body = (batch.Response?.Length ?? 0) <= i
                    ? null
                    : batch.Response![i];

                // last batch
                if (blockInfo is null)
                {
                    break;
                }

                if (body is not null)
                {
                    Block? block = null;
                    bool isValid = !hasBreachedProtocol && TryPrepareBlock(blockInfo, body, out block);
                    if (isValid)
                    {
                        validResponsesCount++;
                        InsertOneBlock(block!);
                    }
                    else
                    {
                        hasBreachedProtocol = true;
                        if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID - tx or uncles");

                        if (batch.ResponseSourcePeer is not null)
                        {
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, InitiateDisconnectReason.InvalidTxOrUncle, "invalid tx or uncles root");
                        }

                        _syncStatusList.MarkPending(blockInfo);
                    }
                }
                else
                {
                    _syncStatusList.MarkPending(blockInfo);
                }
            }

            UpdateSyncReport();
            AdjustRequestSizes(batch, validResponsesCount);
            LogPostProcessingBatchInfo(batch, validResponsesCount);

            return validResponsesCount;
        }

        private void InsertOneBlock(Block block)
        {
            _blockTree.Insert(block, BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks);
            _syncStatusList.MarkInserted(block.Number);
        }

        private void LogPostProcessingBatchInfo(BodiesSyncBatch batch, int validResponsesCount)
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"{nameof(BodiesSyncBatch)} back from {batch.ResponseSourcePeer} with {validResponsesCount}/{batch.Infos.Length}");
        }

        private void UpdateSyncReport()
        {
            _syncReport.FastBlocksBodies.Update(_pivotNumber - _syncStatusList.LowestInsertWithoutGaps);
            _syncReport.BodiesInQueue.Update(_syncStatusList.QueueSize);
        }

        private void AdjustRequestSizes(BodiesSyncBatch batch, int validResponsesCount)
        {
            lock (_syncStatusList)
            {
                if (validResponsesCount == batch.Infos.Length)
                {
                    _requestSize = Math.Min(256, _requestSize * 2);
                }

                if (validResponsesCount == 0)
                {
                    _requestSize = Math.Max(4, _requestSize / 2);
                }
            }
        }
    }
}
