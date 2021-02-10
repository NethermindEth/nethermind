//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Proofs;
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

        private readonly long _pivotNumber;
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
            if(_logger.IsInfo) _logger.Info($"Using pivot {_pivotNumber} and barrier {_barrier} in bodies sync");
            
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

        public override Task<BodiesSyncBatch?> PrepareRequest()
        {
            BodiesSyncBatch? batch = null;
            if (ShouldBuildANewBatch())
            {
                BlockInfo[] infos = new BlockInfo[_requestSize];
                _syncStatusList.GetInfosForBatch(infos);
                if (infos[0] != null)
                {
                    batch = new BodiesSyncBatch(infos);
                    batch.MinNumber = infos[0].BlockNumber;
                    batch.Prioritized = true;
                }
            }

            _blockTree.LowestInsertedBodyNumber = _syncStatusList.LowestInsertWithoutGaps;

            return Task.FromResult(batch);
        }

        public override SyncResponseHandlingResult HandleResponse(BodiesSyncBatch? batch)
        {
            batch?.MarkHandlingStart();
            try
            {
                if (batch == null)
                {
                    if(_logger.IsDebug) _logger.Debug("Received a NULL batch as a response");
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
            bool ommersHashIsValid = OmmersHash.Calculate(blockBody.Ommers) == header.OmmersHash;
            if (txRootIsValid && ommersHashIsValid)
            {
                block = new Block(header, blockBody);
            }
            else
            {
                block = null;
            }

            return block != null;
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
                if (blockInfo == null)
                {
                    break;
                }

                if (body != null)
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
                        if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID - tx or ommers");

                        if (batch.ResponseSourcePeer != null)
                        {
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, "invalid tx or ommers root");
                        }

                        _syncStatusList.MarkUnknown(blockInfo.BlockNumber);
                    }
                }
                else
                {
                    _syncStatusList.MarkUnknown(blockInfo.BlockNumber);
                }
            }

            UpdateSyncReport();
            AdjustRequestSizes(batch, validResponsesCount);
            LogPostProcessingBatchInfo(batch, validResponsesCount);

            return validResponsesCount;
        }
        
        private void InsertOneBlock(Block block)
        {
            _blockTree.Insert(block);
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
