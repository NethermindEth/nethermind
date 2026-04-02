// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Stats.SyncLimits;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.Synchronization.FastBlocks;

public class BlockAccessListsSyncFeed : BarrierSyncFeed<BlockAccessListsSyncBatch?>
{
    protected override long? LowestInsertedNumber => _syncPointers.LowestInsertedAccessListBlockNumber;
    protected override int BarrierWhenStartedMetadataDbKey => MetadataDbKeys.BlockAccessListsBarrierWhenStarted;
    protected override long SyncConfigBarrierCalc => _syncConfig.AncientAccessListsBarrierCalc;
    protected override Func<bool> HasPivot =>
        () => _blockAccessListStore.HasBlock(_blockTree.SyncPivot.BlockHash);

    private readonly FastBlocksAllocationStrategy _approximateAllocationStrategy = new(TransferSpeedType.BlockAccessLists, 0, true);

    private readonly IBlockTree _blockTree;
    private readonly ISyncConfig _syncConfig;
    private readonly ISyncReport _syncReport;
    private readonly IBlockAccessListStore _blockAccessListStore;
    private readonly ISyncPointers _syncPointers;
    private readonly ISyncPeerPool _syncPeerPool;
    private readonly BlockAccessListDownloadStrategy _blockAccessListDownloadStrategy;

    private SyncStatusList _syncStatusList;

    private bool ShouldFinish => !_syncConfig.DownloadAccessListsInFastSync || AllDownloaded;
    private bool AllDownloaded => (_syncPointers.LowestInsertedAccessListBlockNumber ?? long.MaxValue) <= _barrier;

    public override bool IsFinished => AllDownloaded;
    public override string FeedName => nameof(BlockAccessListsSyncFeed);

    public BlockAccessListsSyncFeed(
        ISpecProvider specProvider,
        IBlockTree blockTree,
        IBlockAccessListStore blockAccessListStore,
        ISyncPointers syncPointers,
        ISyncPeerPool syncPeerPool,
        ISyncConfig syncConfig,
        ISyncReport syncReport,
        [KeyFilter(DbNames.Metadata)] IDb metadataDb,
        ILogManager logManager)
        : base(metadataDb, specProvider, logManager?.GetClassLogger() ?? default)
    {
        _blockAccessListStore = blockAccessListStore;
        _syncPointers = syncPointers;
        _syncPeerPool = syncPeerPool;
        _syncConfig = syncConfig;
        _syncReport = syncReport;
        _blockTree = blockTree;
        _blockAccessListDownloadStrategy = new(blockTree, syncReport);

        if (!_syncConfig.FastSync)
        {
            throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
        }

        _pivotNumber = -1; // First reset in `InitializeFeed`.
    }

    public override void InitializeFeed()
    {
        if (_pivotNumber != _blockTree.SyncPivot.BlockNumber || _barrier != _syncConfig.AncientAccessListsBarrierCalc)
        {
            _pivotNumber = _blockTree.SyncPivot.BlockNumber;
            _barrier = _syncConfig.AncientAccessListsBarrierCalc;
            if (_logger.IsInfo) _logger.Info($"Changed pivot in access lists sync. Now using pivot {_pivotNumber} and barrier {_barrier}");
            ResetSyncStatusList();
            InitializeMetadataDb();
        }
        base.InitializeFeed();
        _syncReport.FastBlocksAccessLists.Reset(0, _pivotNumber - _syncConfig.AncientAccessListsBarrierCalc);
    }

    private void ResetSyncStatusList()
    {
        _syncStatusList = new SyncStatusList(
            _blockTree,
            _pivotNumber,
            _syncPointers.LowestInsertedAccessListBlockNumber,
            _syncConfig.AncientAccessListsBarrier);
    }

    protected override SyncMode ActivationSyncModes { get; }
        = SyncMode.FastBlockAccessLists & ~SyncMode.FastBlocks;

    public override bool IsMultiFeed => true;

    public override AllocationContexts Contexts => AllocationContexts.BlockAccessLists;

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
        _syncReport.FastBlocksAccessLists.Update(_pivotNumber);
        _syncReport.FastBlocksAccessLists.MarkEnd();
    }

    public override async Task<BlockAccessListsSyncBatch?> PrepareRequest(CancellationToken token = default)
    {
        BlockAccessListsSyncBatch? batch = null;
        if (ShouldBuildANewBatch())
        {
            int requestSize =
                (await _syncPeerPool.EstimateRequestLimit(RequestType.BlockAccessLists, _approximateAllocationStrategy, AllocationContexts.BlockAccessLists, token))
                ?? GethSyncLimits.MaxBodyFetch;

            BlockInfo?[] infos;
            while (!_syncStatusList.TryGetInfosForBatch(requestSize, _blockAccessListDownloadStrategy, out infos))
            {
                token.ThrowIfCancellationRequested();
                _syncPointers.LowestInsertedAccessListBlockNumber = _syncStatusList.LowestInsertWithoutGaps;
                UpdateSyncReport();
            }

            if (infos[0] is not null)
            {
                batch = new BlockAccessListsSyncBatch(infos)
                {
                    Prioritized = true
                };
            }
        }

        _syncPointers.LowestInsertedAccessListBlockNumber = _syncStatusList.LowestInsertWithoutGaps;

        return batch;
    }

    public override SyncResponseHandlingResult HandleResponse(BlockAccessListsSyncBatch? batch, PeerInfo peer = null)
    {
        batch?.MarkHandlingStart();
        try
        {
            if (batch is null)
            {
                if (_logger.IsDebug) _logger.Debug("Received a NULL batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            int added = InsertAccessLists(batch);
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

    private int InsertAccessLists(BlockAccessListsSyncBatch batch)
    {
        bool hasBreachedProtocol = false;
        int validResponsesCount = 0;

        BlockInfo?[] blockInfos = batch.Infos;
        for (int i = 0; i < blockInfos.Length; i++)
        {
            BlockInfo? blockInfo = blockInfos[i];
            byte[]? accessListRlp = (batch.Response?.Count ?? 0) <= i
                ? null
                : batch.Response![i];

            if (accessListRlp is not null)
            {
                // last batch
                if (blockInfo is null)
                {
                    break;
                }

                if (!hasBreachedProtocol)
                {
                    try
                    {
                        _blockAccessListStore.Insert(blockInfo.BlockHash, accessListRlp);
                        _syncStatusList.MarkInserted(blockInfo.BlockNumber);
                        validResponsesCount++;
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Error inserting access list for block {blockInfo.BlockNumber}: {ex.Message}");
                        _syncStatusList.MarkPending(blockInfo);
                    }
                }
                else
                {
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

    private void LogPostProcessingBatchInfo(BlockAccessListsSyncBatch batch, int validResponsesCount)
    {
        if (_logger.IsDebug)
            _logger.Debug(
                $"{nameof(BlockAccessListsSyncBatch)} back from {batch.ResponseSourcePeer} with {validResponsesCount}/{batch.Infos.Length}");
    }

    private void UpdateSyncReport()
    {
        _syncReport.FastBlocksAccessLists.Update(_pivotNumber - _syncStatusList.LowestInsertWithoutGaps);
        _syncReport.FastBlocksAccessLists.CurrentQueued = _syncStatusList.QueueSize;
    }

    private class BlockAccessListDownloadStrategy(IBlockTree blockTree, ISyncReport syncReport) : IBlockDownloadStrategy
    {
        private long _lowestQueriedBlockWithAccessLists = long.MaxValue;

        public bool ShouldDownloadBlock(BlockInfo info)
        {
            if (info.BlockNumber > Interlocked.Read(ref _lowestQueriedBlockWithAccessLists))
            {
                return true;
            }

            BlockHeader? header = blockTree.FindHeader(info.BlockHash, blockNumber: info.BlockNumber);
            if (header is null)
            {
                return true;
            }

            if (header.BlockAccessListHash is not null)
            {
                long currentLowest = Interlocked.Read(ref _lowestQueriedBlockWithAccessLists);
                while (info.BlockNumber < currentLowest)
                {
                    long previousLowest = Interlocked.CompareExchange(ref _lowestQueriedBlockWithAccessLists, info.BlockNumber, currentLowest);
                    if (previousLowest == currentLowest)
                    {
                        break;
                    }

                    currentLowest = previousLowest;
                }

                return true;
            }

            syncReport.FastBlocksAccessLists.IncrementSkipped();
            return false;
        }
    }
}
