// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
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
        protected override ulong SyncConfigBarrierCalc => ComputeBarrier(_blockTree.SyncPivot.BlockNumber);

        private ulong ComputeBarrier(ulong pivotNumber)
        {
            ulong requested = Math.Max(_syncConfig.AncientBodiesBarrier, _syncConfig.AncientReceiptsBarrier);
            ulong clamped = Math.Max(1UL, Math.Min(pivotNumber, requested));
            ulong? cutoffBlockNumber = _historyPruner.CutoffBlockNumber;
            return cutoffBlockNumber is null ? clamped : ulong.Max(clamped, cutoffBlockNumber.Value);
        }

        protected override Func<bool> HasPivot =>
            () => _receiptStorage.HasBlock(_blockTree.SyncPivot.BlockNumber, _blockTree.SyncPivot.BlockHash);

        private readonly FastBlocksAllocationStrategy _approximateAllocationStrategy = new(TransferSpeedType.Receipts, 0, true);

        private readonly IBlockTree _blockTree;
        private readonly IBlockStore _blockStore;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IReceiptsRootCalculator _receiptsRootCalculator;
        private readonly ISyncPointers _syncPointers;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly IHistoryPruner _historyPruner;
        private readonly ReceiptDownloadStrategy _receiptDownloadStrategy;
        private bool _receiptStorageTakesEncoded = true;

        private SyncStatusList _syncStatusList;

        private bool ShouldFinish => !_syncConfig.DownloadReceiptsInFastSync || AllDownloaded;
        private bool AllDownloaded => (_syncPointers.LowestInsertedReceiptBlockNumber ?? ulong.MaxValue) <= _barrier;

        public override bool IsFinished => AllDownloaded;
        public override string FeedName => nameof(ReceiptsSyncFeed);

        public ReceiptsSyncFeed(
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IBlockStore blockStore,
            IReceiptStorage receiptStorage,
            IReceiptsRootCalculator receiptsRootCalculator,
            ISyncPointers syncPointers,
            ISyncPeerPool syncPeerPool,
            ISyncConfig syncConfig,
            ISyncReport syncReport,
            IHistoryPruner historyPruner,
            [KeyFilter(DbNames.Metadata)] IDb metadataDb,
            ILogManager logManager)
            : base(metadataDb, specProvider, logManager?.GetClassLogger<ReceiptsSyncFeed>() ?? default)
        {
            _receiptStorage = receiptStorage;
            _receiptsRootCalculator = receiptsRootCalculator;
            _syncPointers = syncPointers;
            _syncPeerPool = syncPeerPool;
            _syncConfig = syncConfig;
            _syncReport = syncReport;
            _blockTree = blockTree;
            _blockStore = blockStore;
            _historyPruner = historyPruner;
            _receiptDownloadStrategy = new(_blockTree, _receiptStorage, _syncReport, _historyPruner);

            if (!_syncConfig.FastSync)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = 0; // First reset in `InitializeFeed`.
        }

        public override void InitializeFeed()
        {
            ulong newPivotNumber = _blockTree.SyncPivot.BlockNumber;
            ulong newBarrier = ComputeBarrier(newPivotNumber);
            if (_pivotNumber != newPivotNumber || _barrier != newBarrier)
            {
                _pivotNumber = newPivotNumber;
                _barrier = newBarrier;
                if (_logger.IsInfo) _logger.Info($"Changed pivot in receipts sync. Now using pivot {_pivotNumber} and barrier {_barrier}");
                ResetSyncStatusList();
                InitializeMetadataDb();
            }
            base.InitializeFeed();
            _syncReport.FastBlocksReceipts.Reset(0, _pivotNumber - _barrier);
        }

        private void ResetSyncStatusList() => _syncStatusList = new SyncStatusList(
                _blockTree,
                _pivotNumber,
                _syncPointers.LowestInsertedReceiptBlockNumber,
                _barrier);

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
            _syncReport.FastBlocksReceipts.Update(_pivotNumber - _barrier);
            _syncReport.FastBlocksReceipts.MarkEnd();
        }

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
                if (batch.RequestTimedOut) return SyncResponseHandlingResult.Emptish;
                if (added != 0) return SyncResponseHandlingResult.OK;
                if (batch.Response?.Count == 0)
                {
                    if (FastBlocksBatch.IsBelowAvailableHistory(batch.Infos, batch.EarliestBlockNumberAtRequest))
                        return SyncResponseHandlingResult.Emptish;
                    PeerInfo? source = batch.ResponseSourcePeer ?? peer;
                    if (source?.RecordHistoryEmpty(AllocationContexts.Receipts, batch.Infos) == true)
                        return SyncResponseHandlingResult.Emptish;
                }
                return SyncResponseHandlingResult.NoProgress;
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

        private enum ReceiptPreparationOutcome
        {
            Invalid,
            MissingHeader,
            Encoded,
            Decoded
        }

        private readonly record struct PreparedReceipts(ReceiptPreparationOutcome Outcome, BlockHeader? Header = null, TxReceipt[]? Receipts = null);

        private PreparedReceipts PrepareReceipts(BlockInfo blockInfo, IOwnedReadOnlyList<TxReceipt[]?> response, int index)
        {
            BlockHeader? header = _blockTree.FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded, blockNumber: blockInfo.BlockNumber);
            if (header is null)
            {
                if (_logger.IsDebug) _logger.Debug($"Could not find header {blockInfo.BlockNumber} ({blockInfo.BlockHash})");
                return new(ReceiptPreparationOutcome.MissingHeader);
            }

            // The header carries the timestamp that timestamp-activated receipt rules (EIP-7668) need.
            IReceiptSpec releaseSpec = _specProvider.GetSpec(header);
            if (response is NetworkBlockReceipts network && IsReceiptsRoot(network, index, releaseSpec, header.ReceiptsRoot))
            {
                return new(ReceiptPreparationOutcome.Encoded, header);
            }

            TxReceipt[] receipts;
            try
            {
                receipts = response[index] ?? [];
            }
            catch (RlpReaderException e)
            {
                // Receipts kept encoded are decoded on first read, so a malformed response fails here.
                if (_logger.IsDebug) _logger.Debug($"Receipts of {blockInfo.BlockNumber} ({blockInfo.BlockHash}) do not decode: {e.Message}");
                return new(ReceiptPreparationOutcome.Invalid);
            }

            if (header.ReceiptsRoot == Keccak.EmptyTreeHash)
            {
                return receipts.Length == 0
                    ? new(ReceiptPreparationOutcome.Decoded, header, receipts)
                    : new(ReceiptPreparationOutcome.Invalid);
            }

            // EIP-7668 roots ignore receipt blooms, so there is nothing to normalize.
            if (!releaseSpec.IsEip7668Enabled) NormalizeZeroBlooms(receipts);
            return _receiptsRootCalculator.GetReceiptsRoot(receipts, releaseSpec, header.ReceiptsRoot) == header.ReceiptsRoot
                ? new(ReceiptPreparationOutcome.Decoded, header, receipts)
                : new(ReceiptPreparationOutcome.Invalid);
        }

        /// <returns><see langword="false"/> when the receipt store does not take encoded receipts.</returns>
        /// <exception cref="InvalidDataException">The receipts do not fit the block.</exception>
        private bool TryInsertKeptEncoded(IReceiptsInsertBatch insertBatch, BlockHeader header, MemoryManager<byte> blockRlp, NetworkBlockReceipts receipts, int index)
        {
            Transaction[] transactions = BlockBodyDecoder.DecodeEncodedTransactionsOfBlock(blockRlp.Memory);
            try
            {
                return _receiptStorageTakesEncoded = insertBatch.TryInsertNetworkReceipts(
                    new Block(header, new BlockBody(transactions, [])), receipts.GetEncoded(index), receipts.WithBloom);
            }
            finally
            {
                foreach (Transaction transaction in transactions) TxDecoder.TxObjectPool.Return(transaction);
            }
        }

        private TxReceipt[] DecodeKeptEncoded(BlockHeader header, NetworkBlockReceipts receipts, int index)
        {
            TxReceipt[] decoded = receipts[index];
            // The encoded root check takes the bloom of the logs for an all-zero one, so the decoded receipts need it too.
            if (!_specProvider.GetSpec(header).IsEip7668Enabled) NormalizeZeroBlooms(decoded);
            return decoded;
        }

        /// <remarks>A root that differs is checked again on the decoded receipts, which the root calculator may normalize.</remarks>
        private bool IsReceiptsRoot(NetworkBlockReceipts receipts, int index, IReceiptSpec spec, Hash256? receiptsRoot)
        {
            ReadOnlyMemory<byte> encoded = receipts.GetEncoded(index);
            return receiptsRoot == Keccak.EmptyTreeHash
                ? encoded.Length == 1
                : _receiptsRootCalculator.GetReceiptsRoot(encoded, receipts.WithBloom, spec) is { } root && root == receiptsRoot;
        }

        /// <summary>
        /// Recomputes a receipt's bloom from its logs when a peer ships an all-zero bloom for a
        /// receipt that has logs. A non-zero but wrong bloom is left alone and caught later by the
        /// receipts-root comparison.
        /// </summary>
        public static void NormalizeZeroBlooms(TxReceipt[] receipts)
        {
            for (int i = 0; i < receipts.Length; i++)
            {
                TxReceipt receipt = receipts[i];
                if ((receipt.Logs?.Length ?? 0) > 0 && receipt.Bloom == Bloom.Empty)
                {
                    receipt.Bloom = receipt.CalculateBloom();
                }
            }
        }

        private Block? FindBlock(BlockInfo blockInfo) =>
            _blockTree.FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded, blockNumber: blockInfo.BlockNumber);

        private int InsertReceipts(ReceiptsSyncBatch batch)
        {
            using ArrayPoolList<ulong> insertedBlocks = new(batch.Infos.Length);
            IReceiptsInsertBatch insertBatch = _receiptStorage.StartInsertBatch();
            int validResponsesCount;
            try
            {
                validResponsesCount = AddReceipts(batch, insertBatch, insertedBlocks);
            }
            catch (Exception)
            {
                if (TryStore(insertBatch)) MarkInserted(insertedBlocks);
                throw;
            }

            insertBatch.Dispose();
            MarkInserted(insertedBlocks);
            UpdateSyncReport();
            LogPostProcessingBatchInfo(batch, validResponsesCount);
            return validResponsesCount;
        }

        private bool TryStore(IReceiptsInsertBatch insertBatch)
        {
            try
            {
                insertBatch.Dispose();
                return true;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Could not store the receipts inserted before the failure.", e);
                return false;
            }
        }

        private void MarkInserted(ArrayPoolList<ulong> insertedBlocks)
        {
            foreach (ulong blockNumber in insertedBlocks)
            {
                _syncStatusList.MarkInserted(blockNumber);
            }
        }

        private int AddReceipts(ReceiptsSyncBatch batch, IReceiptsInsertBatch insertBatch, ArrayPoolList<ulong> insertedBlocks)
        {
            bool hasBreachedProtocol = false;
            int validResponsesCount = 0;
            MissedBlocks missingHeaders = new();
            MissedBlocks missingBlocks = new();

            BlockInfo?[] blockInfos = batch.Infos;
            for (int i = 0; i < blockInfos.Length; i++)
            {
                BlockInfo? blockInfo = blockInfos[i];
                if ((batch.Response?.Count ?? 0) > i)
                {
                    // last batch
                    if (blockInfo is null)
                    {
                        break;
                    }

                    PreparedReceipts prepared = hasBreachedProtocol ? default : PrepareReceipts(blockInfo, batch.Response!, i);
                    if (prepared.Outcome is ReceiptPreparationOutcome.Encoded or ReceiptPreparationOutcome.Decoded)
                    {
                        bool keptEncoded = prepared.Outcome == ReceiptPreparationOutcome.Encoded;
                        // Receipts kept encoded need the block's transaction hashes only; the decoded ones go through
                        // receipt recovery, which reads the transactions' recipients.
                        using MemoryManager<byte>? blockRlp = keptEncoded && _receiptStorageTakesEncoded ? _blockStore.GetOwnedRlp(blockInfo.BlockNumber, blockInfo.BlockHash) : null;
                        Block? block = blockRlp is null ? FindBlock(blockInfo) : null;
                        if (blockRlp is null && block is null)
                        {
                            if (blockInfo.BlockNumber >= _barrier)
                            {
                                missingBlocks.Add(blockInfo.BlockNumber);
                                if (_logger.IsDebug) _logger.Debug($"Could not find block {blockInfo.BlockNumber} ({blockInfo.BlockHash})");
                            }

                            _syncStatusList.MarkPending(blockInfo);
                        }
                        else
                        {
                            try
                            {
                                if (blockRlp is null || !TryInsertKeptEncoded(insertBatch, prepared.Header!, blockRlp, (NetworkBlockReceipts)batch.Response!, i))
                                {
                                    block ??= FindBlock(blockInfo) ?? throw new InvalidDataException($"Block {blockInfo.BlockNumber} ({blockInfo.BlockHash}) is not stored.");
                                    TxReceipt[] receipts = keptEncoded
                                        ? DecodeKeptEncoded(prepared.Header!, (NetworkBlockReceipts)batch.Response!, i)
                                        : prepared.Receipts!;
                                    insertBatch.Insert(block, receipts);
                                }

                                insertedBlocks.Add(blockInfo.BlockNumber);
                                validResponsesCount++;
                                // Receipts inserted still encoded never load the block.
                                batch.ResponseSourcePeer?.RecordHistoryData(AllocationContexts.Receipts, blockInfo.BlockNumber);
                            }
                            catch (InvalidDataException)
                            {
                                _syncStatusList.MarkPending(blockInfo);
                            }
                        }
                    }
                    else if (prepared.Outcome == ReceiptPreparationOutcome.MissingHeader)
                    {
                        missingHeaders.Add(blockInfo.BlockNumber);
                        _syncStatusList.MarkPending(blockInfo);
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

            // The batch is re-pended and retried, so warn once per batch rather than once per block.
            if (_logger.IsWarn)
            {
                if (missingHeaders.Count > 0) _logger.Warn($"Could not find headers of {missingHeaders}");
                if (missingBlocks.Count > 0) _logger.Warn($"Could not find {missingBlocks}");
            }

            return validResponsesCount;
        }

        /// <summary>Counts the blocks of a batch that were missing locally and names the range they span.</summary>
        private struct MissedBlocks
        {
            private ulong _lowest;
            private ulong _highest;

            public int Count { get; private set; }

            public void Add(ulong blockNumber)
            {
                _lowest = Count == 0 ? blockNumber : Math.Min(_lowest, blockNumber);
                _highest = Math.Max(_highest, blockNumber);
                Count++;
            }

            public override readonly string ToString() =>
                Count == 1 ? $"block {_lowest}" : $"{Count} blocks in range {_lowest}-{_highest}";
        }

        private void LogPostProcessingBatchInfo(ReceiptsSyncBatch batch, int validResponsesCount)
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"{nameof(ReceiptsSyncBatch)} back from {batch.ResponseSourcePeer} with {validResponsesCount}/{batch.Infos.Length}");
        }

        private void UpdateSyncReport()
        {
            _syncReport.FastBlocksReceipts.Update(_pivotNumber - _syncStatusList.LowestInsertWithoutGaps);
            _syncReport.FastBlocksReceipts.CurrentQueued = _syncStatusList.QueueSize;
        }

        private class ReceiptDownloadStrategy(IBlockTree blockTree, IReceiptStorage receiptStorage, ISyncReport syncReport, IHistoryPruner historyPruner) : IBlockDownloadStrategy
        {
            public bool ShouldDownloadBlock(BlockInfo info)
            {
                bool hasReceipt = receiptStorage.HasBlock(info.BlockNumber, info.BlockHash);
                ulong? cutoff = historyPruner?.CutoffBlockNumber;
                cutoff = cutoff is null ? null : ulong.Min(cutoff!.Value, blockTree.SyncPivot.BlockNumber);
                bool shouldDownload = !hasReceipt && (cutoff is null || info.BlockNumber >= cutoff);
                if (!shouldDownload) syncReport.FastBlocksReceipts.IncrementSkipped();
                return shouldDownload;
            }
        }
    }
}
