// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Stats.SyncLimits;
using Nethermind.History;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.Synchronization.FastBlocks
{
    public class ReceiptsSyncFeed : BarrierSyncFeed<ReceiptsSyncBatch?>
    {
        protected override ulong? LowestInsertedNumber => _syncPointers.LowestInsertedReceiptBlockNumber;
        protected override int BarrierWhenStartedMetadataDbKey => MetadataDbKeys.ReceiptsBarrierWhenStarted;
        protected override long SyncConfigBarrierCalc
        {
            get
            {
                long? cutoffBlockNumber = _historyPruner.CutoffBlockNumber;
                return cutoffBlockNumber is null ? _syncConfig.AncientBodiesBarrierCalc : Math.Max(_syncConfig.AncientBodiesBarrierCalc, cutoffBlockNumber.Value);
            }
        }
        protected override Func<bool> HasPivot =>
            () => _receiptStorage.HasBlock(_blockTree.SyncPivot.BlockNumber, _blockTree.SyncPivot.BlockHash);

        private readonly FastBlocksAllocationStrategy _approximateAllocationStrategy = new(TransferSpeedType.Receipts, 0, true);

        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISyncPointers _syncPointers;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly IHistoryPruner _historyPruner;
        private readonly ReceiptDownloadStrategy _receiptDownloadStrategy;

        private SyncStatusList _syncStatusList;

        private bool ShouldFinish => !_syncConfig.DownloadReceiptsInFastSync || AllDownloaded;
        private bool AllDownloaded => (_syncPointers.LowestInsertedReceiptBlockNumber ?? ulong.MaxValue) <= _barrier;

        public override bool IsFinished => AllDownloaded;
        public override string FeedName => nameof(ReceiptsSyncFeed);

        public ReceiptsSyncFeed(
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ISyncPointers syncPointers,
            ISyncPeerPool syncPeerPool,
            ISyncConfig syncConfig,
            ISyncReport syncReport,
            IHistoryPruner historyPruner,
            [KeyFilter(DbNames.Metadata)] IDb metadataDb,
            ILogManager logManager)
            : base(metadataDb, specProvider, logManager?.GetClassLogger() ?? default)
        {
            _receiptStorage = receiptStorage;
            _syncPointers = syncPointers;
            _syncPeerPool = syncPeerPool;
            _syncConfig = syncConfig;
            _syncReport = syncReport;
            _blockTree = blockTree;
            _historyPruner = historyPruner;
            _receiptDownloadStrategy = new(_blockTree, _receiptStorage, _syncReport, _historyPruner);

            if (!_syncConfig.FastSync)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = ulong.MaxValue; // First reset in `InitializeFeed`.
        }

        public override void InitializeFeed()
        {
            ulong barrierCalc = ToUlongOrZero(_syncConfig.AncientReceiptsBarrierCalc);
            if (_pivotNumber != _blockTree.SyncPivot.BlockNumber || _barrier != barrierCalc)
            {
                _pivotNumber = _blockTree.SyncPivot.BlockNumber;
                _barrier = barrierCalc;
                if (_logger.IsInfo) _logger.Info($"Changed pivot in receipts sync. Now using pivot {_pivotNumber} and barrier {_barrier}");
                ResetSyncStatusList();
                InitializeMetadataDb();
            }
            base.InitializeFeed();

            // `_pivotNumber` and `barrierCalc` are `ulong`. If the barrier moves ahead (reorg/pruning/config),
            // a raw subtraction would wrap around and later progress reporting would overflow.
            ulong toDownload = _pivotNumber > barrierCalc ? _pivotNumber - barrierCalc : 0UL;
            _syncReport.FastBlocksReceipts.Reset(0, ClampToLong(toDownload));
        }

        private void ResetSyncStatusList()
        {
            _syncStatusList = new SyncStatusList(
                _blockTree,
                _pivotNumber,
                _syncPointers.LowestInsertedReceiptBlockNumber,
                ToUlongOrZero(_syncConfig.AncientReceiptsBarrier));
        }

        protected override SyncMode ActivationSyncModes { get; }
            = SyncMode.FastReceipts & ~SyncMode.FastBlocks;

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.Receipts;

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
            // Progress logger expects `long`; clamp to avoid `OverflowException` on very large pivots.
            _syncReport.FastBlocksReceipts.Update(ClampToLong(_pivotNumber));
            _syncReport.FastBlocksReceipts.MarkEnd();
        }

        private static long ClampToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

        private static ulong ToUlongOrZero(long value) => value > 0 ? (ulong)value : 0UL;

        public override async Task<ReceiptsSyncBatch?> PrepareRequest(CancellationToken token = default)
        {
            ReceiptsSyncBatch? batch = null;
            if (ShouldBuildANewBatch())
            {
                // Set the request size depending on the approximate allocation strategy.
                int requestSize =
                    (await _syncPeerPool.EstimateRequestLimit(RequestType.Receipts, _approximateAllocationStrategy, AllocationContexts.Receipts, token))
                    ?? GethSyncLimits.MaxReceiptFetch;

                BlockInfo?[] infos;
                while (!_syncStatusList.TryGetInfosForBatch(requestSize, _receiptDownloadStrategy, out infos))
                {
                    token.ThrowIfCancellationRequested();
                    _syncPointers.LowestInsertedReceiptBlockNumber = _syncStatusList.LowestInsertWithoutGaps;
                    UpdateSyncReport();
                }

                if (infos[0] is not null)
                {
                    batch = new ReceiptsSyncBatch(infos)
                    {
                        Prioritized = true
                    };
                }
            }

            _syncPointers.LowestInsertedReceiptBlockNumber = _syncStatusList.LowestInsertWithoutGaps;

            return batch;
        }

        public override SyncResponseHandlingResult HandleResponse(ReceiptsSyncBatch? batch, PeerInfo peer = null)
        {
            batch?.MarkHandlingStart();
            try
            {
                if (batch is null)
                {
                    if (_logger.IsDebug) _logger.Debug("Received a NULL batch as a response");
                    return SyncResponseHandlingResult.InternalError;
                }

                int added = InsertReceipts(batch);
                return added == 0 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.OK;
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

        private bool TryPrepareReceipts(BlockInfo blockInfo, TxReceipt[] receipts, out TxReceipt[]? preparedReceipts)
        {
            BlockHeader? header = _blockTree.FindHeader(blockInfo.BlockHash, blockNumber: blockInfo.BlockNumber);
            if (header is null)
            {
                if (_logger.IsWarn) _logger.Warn("Could not find header for requested blockhash.");
                preparedReceipts = null;
            }
            else
            {
                if (header.ReceiptsRoot == Keccak.EmptyTreeHash)
                {
                    preparedReceipts = receipts.Length == 0 ? receipts : null;
                }
                else
                {
                    // BlockInfo has no timestamp
                    IReceiptSpec releaseSpec = _specProvider.GetReceiptSpec(blockInfo.BlockNumber);
                    // TODO: Optimism use op root calculator
                    preparedReceipts = ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, releaseSpec, header.ReceiptsRoot) != header.ReceiptsRoot
                        ? null
                        : receipts;
                }
            }

            return preparedReceipts is not null;
        }

        private int InsertReceipts(ReceiptsSyncBatch batch)
        {
            bool hasBreachedProtocol = false;
            int validResponsesCount = 0;

            BlockInfo?[] blockInfos = batch.Infos;
            for (int i = 0; i < blockInfos.Length; i++)
            {
                BlockInfo? blockInfo = blockInfos[i];
                TxReceipt[]? receipts = (batch.Response?.Count ?? 0) <= i
                    ? null
                    : (batch.Response![i] ?? []);

                if (receipts is not null)
                {
                    TxReceipt[]? prepared = null;
                    // last batch
                    if (blockInfo is null)
                    {
                        break;
                    }

                    bool isValid = !hasBreachedProtocol && TryPrepareReceipts(blockInfo, receipts, out prepared);
                    if (isValid)
                    {
                        Block? block = _blockTree.FindBlock(blockInfo.BlockHash);
                        if (block is null)
                        {
                            if (blockInfo.BlockNumber >= _barrier)
                            {
                                if (_logger.IsWarn) _logger.Warn($"Could not find block {blockInfo.BlockHash}");
                            }

                            _syncStatusList.MarkPending(blockInfo);
                        }
                        else
                        {
                            try
                            {
                                _receiptStorage.Insert(block, prepared, ensureCanonical: true);
                                _syncStatusList.MarkInserted(block.Number);
                                validResponsesCount++;
                            }
                            catch (InvalidDataException)
                            {
                                _syncStatusList.MarkPending(blockInfo);
                            }
                        }
                    }
                    else
                    {
                        hasBreachedProtocol = true;
                        if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID - tx or uncles");

                        if (batch.ResponseSourcePeer is not null)
                        {
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, DisconnectReason.InvalidReceiptRoot, "invalid tx or uncles root");
                        }

                        _syncStatusList.MarkPending(blockInfo);
                    }
                }
                else
                {
                    if (blockInfo is not null)
                    {
                        _syncStatusList.MarkPending(blockInfo);
                    }
                }
            }

            UpdateSyncReport();
            LogPostProcessingBatchInfo(batch, validResponsesCount);
            return validResponsesCount;
        }

        private void LogPostProcessingBatchInfo(ReceiptsSyncBatch batch, int validResponsesCount)
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"{nameof(ReceiptsSyncBatch)} back from {batch.ResponseSourcePeer} with {validResponsesCount}/{batch.Infos.Length}");
        }

        private void UpdateSyncReport()
        {
            ulong behindPivot = _pivotNumber > _syncStatusList.LowestInsertWithoutGaps
                ? _pivotNumber - _syncStatusList.LowestInsertWithoutGaps
                : 0UL;
            _syncReport.FastBlocksReceipts.Update(ClampToLong(behindPivot));
            _syncReport.FastBlocksReceipts.CurrentQueued = _syncStatusList.QueueSize;
        }

        private class ReceiptDownloadStrategy(IBlockTree blockTree, IReceiptStorage receiptStorage, ISyncReport syncReport, IHistoryPruner historyPruner) : IBlockDownloadStrategy
        {
            public bool ShouldDownloadBlock(BlockInfo info)
            {
                bool hasReceipt = receiptStorage.HasBlock(info.BlockNumber, info.BlockHash);
                long? cutoffLong = historyPruner?.CutoffBlockNumber;
                ulong? cutoff = cutoffLong is null
                    ? null
                    : Math.Min(ToUlongOrZero(cutoffLong.Value), blockTree.SyncPivot.BlockNumber);
                bool shouldDownload = !hasReceipt && (cutoff is null || info.BlockNumber >= cutoff);
                if (!shouldDownload) syncReport.FastBlocksBodies.IncrementSkipped();
                return shouldDownload;
            }
        }
    }
}
