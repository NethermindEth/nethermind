// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class BodiesSyncFeed : BarrierSyncFeed<BodiesSyncBatch?>
    {
        protected override long? LowestInsertedNumber => _syncPointers.LowestInsertedBodyNumber;
        protected override int BarrierWhenStartedMetadataDbKey => MetadataDbKeys.BodiesBarrierWhenStarted;
        protected override long SyncConfigBarrierCalc => _syncConfig.AncientBodiesBarrierCalc;
        protected override Func<bool> HasPivot =>
            () => _syncPointers.LowestInsertedBodyNumber is not null && _syncPointers.LowestInsertedBodyNumber <= _blockTree.SyncPivot.BlockNumber;

        private FastBlocksAllocationStrategy _approximateAllocationStrategy = new FastBlocksAllocationStrategy(TransferSpeedType.Bodies, 0, true);

        private const long DefaultFlushDbInterval = 100000; // About every 10GB on mainnet
        private readonly long _flushDbInterval; // About every 10GB on mainnet

        private readonly IBlockTree _blockTree;
        private readonly IBlockValidator _blockValidator;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly ISyncPointers _syncPointers;
        private readonly IDb _blocksDb;

        private SyncStatusList _syncStatusList;

        private bool ShouldFinish => !_syncConfig.DownloadBodiesInFastSync || AllDownloaded;
        private bool AllDownloaded => (_syncPointers.LowestInsertedBodyNumber ?? long.MaxValue) <= _barrier
            || WithinOldBarrierDefault;

        public override bool IsFinished => AllDownloaded;
        public BodiesSyncFeed(
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            ISyncPointers syncPointers,
            ISyncPeerPool syncPeerPool,
            ISyncConfig syncConfig,
            ISyncReport syncReport,
            [KeyFilter(DbNames.Blocks)] IDb blocksDb,
            [KeyFilter(DbNames.Metadata)] IDb metadataDb,
            ILogManager logManager,
            long flushDbInterval = DefaultFlushDbInterval)
            : base(metadataDb, specProvider, logManager.GetClassLogger())
        {
            _blockTree = blockTree;
            _blockValidator = blockValidator;
            _syncPointers = syncPointers;
            _syncPeerPool = syncPeerPool;
            _syncConfig = syncConfig;
            _syncReport = syncReport;
            _blocksDb = blocksDb;
            _flushDbInterval = flushDbInterval;

            if (!_syncConfig.FastSync)
            {
                throw new InvalidOperationException("Entered fast bodies mode without fast sync enabled in configuration.");
            }

            _pivotNumber = -1; // First reset in `InitializeFeed`.
        }

        public override void InitializeFeed()
        {
            if (_pivotNumber != _blockTree.SyncPivot.BlockNumber || _barrier != _syncConfig.AncientBodiesBarrierCalc)
            {
                _pivotNumber = _blockTree.SyncPivot.BlockNumber;
                _barrier = _syncConfig.AncientBodiesBarrierCalc;
                if (_logger.IsInfo) _logger.Info($"Changed pivot in bodies sync. Now using pivot {_pivotNumber} and barrier {_barrier}");
                ResetSyncStatusList();
                InitializeMetadataDb();
            }
            base.InitializeFeed();
            _syncReport.FastBlocksBodies.Reset(0, _pivotNumber - _syncConfig.AncientBodiesBarrierCalc);
        }

        private void ResetSyncStatusList()
        {
            _syncStatusList = new SyncStatusList(
                _blockTree,
                _pivotNumber,
                _syncPointers.LowestInsertedBodyNumber,
                _syncConfig.AncientBodiesBarrier);
        }

        protected override SyncMode ActivationSyncModes { get; } = SyncMode.FastBodies & ~SyncMode.FastBlocks;

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.Bodies;

        private bool ShouldBuildANewBatch()
        {
            if (ShouldFinish)
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
            Flush();
        }

        public override async Task<BodiesSyncBatch?> PrepareRequest(CancellationToken token = default)
        {
            BodiesSyncBatch? batch = null;
            if (ShouldBuildANewBatch())
            {
                BlockInfo?[] infos = null;

                // Set the request size depending on the approximate allocation strategy.
                int requestSize =
                    (await _syncPeerPool.EstimateRequestLimit(RequestType.Bodies, _approximateAllocationStrategy, AllocationContexts.Bodies, token))
                    ?? GethSyncLimits.MaxBodyFetch;

                while (!_syncStatusList.TryGetInfosForBatch(requestSize, (info) =>
                       {
                           bool hasBlock = _blockTree.HasBlock(info.BlockNumber, info.BlockHash);
                           if (hasBlock) _syncReport.FastBlocksBodies.IncrementSkipped();
                           return hasBlock;
                       }, out infos))
                {
                    token.ThrowIfCancellationRequested();

                    // Otherwise, the progress does not update correctly
                    _syncPointers.LowestInsertedBodyNumber = _syncStatusList.LowestInsertWithoutGaps;
                    UpdateSyncReport();
                }

                if (infos[0] is not null)
                {
                    batch = new BodiesSyncBatch(infos);
                    // Used for peer allocation. It pick peer which have the at least this number
                    batch.Prioritized = true;
                }
            }

            if (
                (_syncPointers.LowestInsertedBodyNumber ?? long.MaxValue) - _syncStatusList.LowestInsertWithoutGaps > _flushDbInterval ||
                _syncStatusList.LowestInsertWithoutGaps <= _barrier // Other state depends on LowestInsertedBodyNumber, so this need to flush or it wont finish
            )
            {
                Flush();
            }

            return batch;
        }

        private void Flush()
        {
            long lowestInsertedAtPoint = _syncStatusList.LowestInsertWithoutGaps;
            _blocksDb.Flush();
            _syncPointers.LowestInsertedBodyNumber = lowestInsertedAtPoint;
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
            catch (Exception)
            {
                foreach (BlockInfo? batchInfo in batch.Infos)
                {
                    if (batchInfo is null) break;
                    _syncStatusList.MarkPending(batchInfo);
                }

                throw;
            }
            finally
            {
                batch?.Dispose();
                batch?.MarkHandlingEnd();
            }
        }

        private bool TryPrepareBlock(BlockInfo blockInfo, BlockBody blockBody, out Block? block)
        {
            BlockHeader header = _blockTree.FindHeader(blockInfo.BlockHash, blockNumber: blockInfo.BlockNumber);
            if (_blockValidator.ValidateBodyAgainstHeader(header, blockBody, out _))
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
            BlockBody[]? responses = batch.Response?.Bodies ?? [];

            for (int i = 0; i < batch.Infos.Length; i++)
            {
                BlockInfo? blockInfo = batch.Infos[i];
                BlockBody? body = responses.Length <= i
                    ? null
                    : responses[i];

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
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, DisconnectReason.InvalidTxOrUncle, "invalid tx or uncles root");
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
            LogPostProcessingBatchInfo(batch, validResponsesCount);

            return validResponsesCount;
        }

        private void InsertOneBlock(Block block)
        {
            _blockTree.Insert(block, BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks, bodiesWriteFlags: WriteFlags.DisableWAL);
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
            _syncReport.FastBlocksBodies.CurrentQueued = _syncStatusList.QueueSize;
        }
    }
}
