// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SyncLimits;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.Synchronization.FastBlocks
{

    public class ReceiptsSyncFeed : BarrierSyncFeed<ReceiptsSyncBatch?>
    {
        protected override long? LowestInsertedNumber => _receiptStorage.LowestInsertedReceiptBlockNumber;
        protected override int BarrierWhenStartedMetadataDbKey => MetadataDbKeys.ReceiptsBarrierWhenStarted;
        protected override long SyncConfigBarrierCalc => _syncConfig.AncientReceiptsBarrierCalc;
        protected override Func<bool> HasPivot =>
            () => _receiptStorage.HasBlock(_syncConfig.PivotNumberParsed, _syncConfig.PivotHashParsed);

        private int _requestSize = GethSyncLimits.MaxReceiptFetch;

        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISyncPeerPool _syncPeerPool;

        private SyncStatusList _syncStatusList;

        private bool ShouldFinish => !_syncConfig.DownloadReceiptsInFastSync || AllDownloaded;
        private bool AllDownloaded => (_receiptStorage.LowestInsertedReceiptBlockNumber ?? long.MaxValue) <= _barrier
            || WithinOldBarrierDefault;

        public override bool IsFinished => AllDownloaded;

        public ReceiptsSyncFeed(
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ISyncPeerPool syncPeerPool,
            ISyncConfig syncConfig,
            ISyncReport syncReport,
            IDb metadataDb,
            ILogManager logManager)
            : base(metadataDb, specProvider, logManager?.GetClassLogger() ?? default)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

            if (!_syncConfig.FastSync)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = -1; // First reset in `InitializeFeed`.
        }

        public override void InitializeFeed()
        {
            if (_pivotNumber != _syncConfig.PivotNumberParsed || _barrier != _syncConfig.AncientReceiptsBarrierCalc)
            {
                _pivotNumber = _syncConfig.PivotNumberParsed;
                _barrier = _syncConfig.AncientReceiptsBarrierCalc;
                if (_logger.IsInfo) _logger.Info($"Changed pivot in receipts sync. Now using pivot {_pivotNumber} and barrier {_barrier}");
                ResetSyncStatusList();
                InitializeMetadataDb();
            }
            base.InitializeFeed();
        }

        private void ResetSyncStatusList()
        {
            _syncStatusList = new SyncStatusList(
                _blockTree,
                _pivotNumber,
                _receiptStorage.LowestInsertedReceiptBlockNumber,
                _syncConfig.AncientReceiptsBarrier);
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
            _syncReport.FastBlocksReceipts.Update(_pivotNumber);
            _syncReport.FastBlocksReceipts.MarkEnd();
            _syncReport.ReceiptsInQueue.Update(0);
            _syncReport.ReceiptsInQueue.MarkEnd();
        }

        public override Task<ReceiptsSyncBatch?> PrepareRequest(CancellationToken token = default)
        {
            ReceiptsSyncBatch? batch = null;
            if (ShouldBuildANewBatch())
            {
                BlockInfo?[] infos = new BlockInfo[_requestSize];
                _syncStatusList.GetInfosForBatch(infos);
                if (infos[0] is not null)
                {
                    batch = new ReceiptsSyncBatch(infos);
                    batch.MinNumber = infos[0].BlockNumber;
                    batch.Prioritized = true;
                }

                // Array.Reverse(infos);
            }

            _receiptStorage.LowestInsertedReceiptBlockNumber = _syncStatusList.LowestInsertWithoutGaps;

            return Task.FromResult(batch);
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
                    : (batch.Response![i] ?? Array.Empty<TxReceipt>());

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
                        Block block = _blockTree.FindBlock(blockInfo.BlockHash);
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

            AdjustRequestSize(batch, validResponsesCount);
            LogPostProcessingBatchInfo(batch, validResponsesCount);

            _syncReport.FastBlocksReceipts.Update(_pivotNumber - _syncStatusList.LowestInsertWithoutGaps);
            _syncReport.ReceiptsInQueue.Update(_syncStatusList.QueueSize);
            return validResponsesCount;
        }

        private void LogPostProcessingBatchInfo(ReceiptsSyncBatch batch, int validResponsesCount)
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"{nameof(ReceiptsSyncBatch)} back from {batch.ResponseSourcePeer} with {validResponsesCount}/{batch.Infos.Length}");
        }

        private void AdjustRequestSize(ReceiptsSyncBatch batch, int validResponsesCount)
        {
            int currentRequestSize = Volatile.Read(ref _requestSize);
            int requestSize = currentRequestSize;
            if (validResponsesCount == batch.Infos.Length)
            {
                requestSize = Math.Min(256, currentRequestSize * 2);
            }

            if (validResponsesCount == 0)
            {
                requestSize = Math.Max(4, currentRequestSize / 2);
            }

            Interlocked.CompareExchange(ref _requestSize, requestSize, currentRequestSize);
        }
    }
}
