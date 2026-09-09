// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
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
    protected override ulong? LowestInsertedNumber => _syncPointers.LowestInsertedBlockAccessListBlockNumber;
    protected override int BarrierWhenStartedMetadataDbKey => MetadataDbKeys.BlockAccessListsBarrierWhenStarted;
    protected override ulong SyncConfigBarrierCalc => _syncConfig.AncientBlockAccessListsBarrierCalc;
    protected override Func<bool> HasPivot =>
        () =>
        {
            BlockHeader? pivotHeader = FindPivotHeader(out ulong pivotNumber, out Hash256 pivotHash);
            return pivotHeader is not null &&
                   (pivotHeader.BlockAccessListHash is null || _blockAccessListStore.Exists(pivotNumber, pivotHash));
        };

    private readonly FastBlocksAllocationStrategy _approximateAllocationStrategy = new(TransferSpeedType.BlockAccessLists, 0, true);

    private readonly IBlockTree _blockTree;
    private readonly ISyncConfig _syncConfig;
    private readonly ISyncReport _syncReport;
    private readonly IBlockAccessListStore _blockAccessListStore;
    private readonly ISyncPointers _syncPointers;
    private readonly ISyncPeerPool _syncPeerPool;
    private readonly BlockAccessListDownloadStrategy _blockAccessListDownloadStrategy;
    private readonly bool _blockAccessListsEverEnabled;

    private SyncStatusList _syncStatusList;

    private bool ShouldFinish => !_syncConfig.DownloadBlockAccessListsInFastSync || !_blockAccessListsEverEnabled || AllDownloaded;
    private bool AllDownloaded => (_syncPointers.LowestInsertedBlockAccessListBlockNumber ?? ulong.MaxValue) <= _barrier;
    private bool PivotHasNoBlockAccessLists => FindPivotHeader(out _, out _) is { BlockAccessListHash: null };

    public override bool IsFinished => !_syncConfig.DownloadBlockAccessListsInFastSync || !_blockAccessListsEverEnabled || AllDownloaded || PivotHasNoBlockAccessLists;
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
        : base(metadataDb, specProvider, logManager?.GetClassLogger<BlockAccessListsSyncFeed>() ?? default)
    {
        _blockAccessListStore = blockAccessListStore;
        _syncPointers = syncPointers;
        _syncPeerPool = syncPeerPool;
        _syncConfig = syncConfig;
        _syncReport = syncReport;
        _blockTree = blockTree;
        _blockAccessListDownloadStrategy = new(blockTree, syncReport);
        _blockAccessListsEverEnabled = specProvider.GetFinalSpec().BlockLevelAccessListsEnabled;

        if (!_syncConfig.FastSync)
        {
            throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
        }

        _pivotNumber = 0; // First reset in `InitializeFeed`.
    }

    public override void InitializeFeed()
    {
        if (_pivotNumber != _blockTree.SyncPivot.BlockNumber || _barrier != _syncConfig.AncientBlockAccessListsBarrierCalc)
        {
            _pivotNumber = _blockTree.SyncPivot.BlockNumber;
            _barrier = _syncConfig.AncientBlockAccessListsBarrierCalc;
            if (_logger.IsInfo) _logger.Info($"Changed pivot in block access lists sync. Now using pivot {_pivotNumber} and barrier {_barrier}");
            ResetSyncStatusList();
            InitializeMetadataDb();
        }
        base.InitializeFeed();
        _syncReport.FastBlockAccessLists.Reset(0, _pivotNumber - _syncConfig.AncientBlockAccessListsBarrierCalc);
    }

    private void ResetSyncStatusList() =>
        _syncStatusList = new SyncStatusList(
            _blockTree,
            _pivotNumber,
            _syncPointers.LowestInsertedBlockAccessListBlockNumber,
            _syncConfig.AncientBlockAccessListsBarrier);

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

        if (PivotHasNoBlockAccessLists)
        {
            FallAsleep();
            return false;
        }

        return true;
    }

    private void PostFinishCleanUp()
    {
        _syncReport.FastBlockAccessLists.Update(_pivotNumber);
        _syncReport.FastBlockAccessLists.MarkEnd();
    }

    private BlockHeader? FindPivotHeader(out ulong pivotNumber, out Hash256 pivotHash)
    {
        (pivotNumber, pivotHash) = _blockTree.SyncPivot;
        return _blockTree.FindHeader(pivotHash, blockNumber: pivotNumber);
    }

    public override async Task<BlockAccessListsSyncBatch?> PrepareRequest(CancellationToken token = default)
    {
        BlockAccessListsSyncBatch? batch = null;
        if (!ShouldBuildANewBatch())
        {
            return null;
        }

        int requestSize =
            (await _syncPeerPool.EstimateRequestLimit(RequestType.BlockAccessLists, _approximateAllocationStrategy, AllocationContexts.BlockAccessLists, token))
            ?? GethSyncLimits.MaxBodyFetch;

        BlockInfo?[] infos;
        while (!_syncStatusList.TryGetInfosForBatch(requestSize, _blockAccessListDownloadStrategy, out infos))
        {
            token.ThrowIfCancellationRequested();
            _syncPointers.LowestInsertedBlockAccessListBlockNumber = _syncStatusList.LowestInsertWithoutGaps;
            UpdateSyncReport();
        }

        if (infos[0] is not null)
        {
            batch = new BlockAccessListsSyncBatch(infos)
            {
                Prioritized = true
            };
        }

        _syncPointers.LowestInsertedBlockAccessListBlockNumber = _syncStatusList.LowestInsertWithoutGaps;

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

            int added = InsertBlockAccessLists(batch);
            return added == 0 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.OK;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

    private bool IsValidAccessList(BlockInfo blockInfo, ReadOnlySpan<byte> accessListRlp, out string? errorMessage)
    {
        BlockHeader? header = _blockTree.FindHeader(blockInfo.BlockHash, blockNumber: blockInfo.BlockNumber);
        bool isValid;
        if (header is null)
        {
            errorMessage = "missing header";
            isValid = false;
        }
        else
        {
            isValid = BlockAccessListHashValidator.Validate(header, accessListRlp, out errorMessage);
        }

        return isValid;
    }

    private int InsertBlockAccessLists(BlockAccessListsSyncBatch batch)
    {
        bool hasBreachedProtocol = false;
        int validResponsesCount = 0;

        BlockInfo?[] blockInfos = batch.Infos;
        for (int i = 0; i < blockInfos.Length; i++)
        {
            BlockInfo? blockInfo = blockInfos[i];
            bool hasAccessListResponse = (batch.Response?.Count ?? 0) > i;
            byte[]? accessListRlp = hasAccessListResponse
                ? batch.Response![i]
                : null;

            if (accessListRlp is not null)
            {
                // last batch
                if (blockInfo is null)
                {
                    break;
                }

                string? errorMessage = null;
                bool isValid = !hasBreachedProtocol && IsValidAccessList(blockInfo, accessListRlp, out errorMessage);
                if (isValid)
                {
                    _blockAccessListStore.Insert(blockInfo.BlockNumber, blockInfo.BlockHash, accessListRlp);
                    _syncStatusList.MarkInserted(blockInfo.BlockNumber);
                    validResponsesCount++;
                }
                else
                {
                    if (!hasBreachedProtocol)
                    {
                        hasBreachedProtocol = true;
                        if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID - {errorMessage}");

                        if (batch.ResponseSourcePeer is not null)
                        {
                            _syncPeerPool.ReportBreachOfProtocol(
                                batch.ResponseSourcePeer,
                                DisconnectReason.InvalidTxOrUncle,
                                $"invalid block access list: {errorMessage}");
                        }
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

    private void LogPostProcessingBatchInfo(BlockAccessListsSyncBatch batch, int validResponsesCount)
    {
        if (_logger.IsDebug)
            _logger.Debug(
                $"{nameof(BlockAccessListsSyncBatch)} back from {batch.ResponseSourcePeer} with {validResponsesCount}/{batch.Infos.Length}");
    }
    private void UpdateSyncReport()
    {
        _syncReport.FastBlockAccessLists.Update(_pivotNumber - _syncStatusList.LowestInsertWithoutGaps);
        _syncReport.FastBlockAccessLists.CurrentQueued = _syncStatusList.QueueSize;
    }

    private class BlockAccessListDownloadStrategy(IBlockTree blockTree, ISyncReport syncReport) : IBlockDownloadStrategy
    {
        private ulong _lowestQueriedBlockWithAccessLists = ulong.MaxValue;

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
                ulong currentLowest = Interlocked.Read(ref _lowestQueriedBlockWithAccessLists);
                while (info.BlockNumber < currentLowest)
                {
                    ulong previousLowest = Interlocked.CompareExchange(ref _lowestQueriedBlockWithAccessLists, info.BlockNumber, currentLowest);
                    if (previousLowest == currentLowest)
                    {
                        break;
                    }

                    currentLowest = previousLowest;
                }

                return true;
            }

            syncReport.FastBlockAccessLists.IncrementSkipped();
            return false;
        }
    }
}
