// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.History;
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
    protected override ulong SyncConfigBarrierCalc =>
        ulong.Max(_syncConfig.AncientBlockAccessListsBarrierCalc, ulong.Min(_historyPruner.BalCutoffBlockNumber ?? 0, _blockTree.SyncPivot.BlockNumber));
    protected override Func<bool> HasPivot =>
        () =>
        {
            BlockHeader? pivotHeader = FindPivotHeader(out ulong pivotNumber, out Hash256 pivotHash);
            return pivotHeader is not null &&
                   (pivotHeader.BlockAccessListHash is null || _blockAccessListStore.Exists(pivotNumber, pivotHash));
        };

    /// <summary>How many different nodes must report an access list unavailable before the height is left as a gap.</summary>
    /// <remarks>
    /// Peers keep access lists only for their own retention window, which can be shorter than this node's, so an old
    /// height may be unavailable everywhere. Leaving it out keeps the feed from stalling; the store then reports it
    /// missing, as it would a pruned one. Heights inside the chain's minimum BAL retention window are never left out,
    /// since every peer must serve them.
    /// </remarks>
    internal const int MaxUnavailableResponses = 8;

    private readonly FastBlocksAllocationStrategy _approximateAllocationStrategy = new(TransferSpeedType.BlockAccessLists, 0, true);
    private readonly ConcurrentDictionary<ulong, HashSet<PublicKey>> _unavailable = new();

    private readonly IBlockTree _blockTree;
    private readonly ISyncConfig _syncConfig;
    private readonly ISyncReport _syncReport;
    private readonly IBlockAccessListStore _blockAccessListStore;
    private readonly ISyncPointers _syncPointers;
    private readonly ISyncPeerPool _syncPeerPool;
    private readonly IHistoryPruner _historyPruner;
    private readonly BlockAccessListDownloadStrategy _blockAccessListDownloadStrategy;
    private readonly bool _blockAccessListsEverEnabled;

    private SyncStatusList _syncStatusList;
    private bool _barrierFallbackLogged;

    private bool ShouldFinish => !_syncConfig.DownloadBlockAccessListsInFastSync || !_blockAccessListsEverEnabled || AllDownloaded;
    private bool AllDownloaded => (_syncPointers.LowestInsertedBlockAccessListBlockNumber ?? ulong.MaxValue) <= _barrier;
    private bool PivotHasNoBlockAccessLists => FindPivotHeader(out _, out _) is { BlockAccessListHash: null };
    private ulong SkippedBelow => ulong.Min(_historyPruner.BalCutoffBlockNumber ?? 0, _blockTree.SyncPivot.BlockNumber);

    internal int UnavailableHeightsCount => _unavailable.Count;

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
        IHistoryPruner historyPruner,
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
        _historyPruner = historyPruner;
        _blockAccessListDownloadStrategy = new(blockTree, syncReport, historyPruner);
        _blockAccessListsEverEnabled = specProvider.GetFinalSpec().BlockLevelAccessListsEnabled;

        if (!_syncConfig.FastSync)
        {
            throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
        }

        _pivotNumber = 0; // First reset in `InitializeFeed`.
        // Resolved before `InitializeFeed` so that `IsFinished` holds after a restart without reactivating the feed.
        _barrier = ResolveBarrier(logFallback: false);
    }

    public override void InitializeFeed()
    {
        ulong barrier = ResolveBarrier(logFallback: true);
        if (_syncStatusList is null || _pivotNumber != _blockTree.SyncPivot.BlockNumber || _barrier != barrier)
        {
            _pivotNumber = _blockTree.SyncPivot.BlockNumber;
            _barrier = barrier;
            if (_logger.IsInfo) _logger.Info($"Changed pivot in block access lists sync. Now using pivot {_pivotNumber} and barrier {_barrier}");
            ResetSyncStatusList();
            InitializeMetadataDb();
        }
        base.InitializeFeed();
        _syncReport.FastBlockAccessLists.Reset(0, _pivotNumber - _barrier);
    }

    /// <summary>
    /// Returns the configured barrier, raised to the block access list pruning cutoff or to the last block before
    /// EIP-7928 activation when either is higher.
    /// </summary>
    /// <remarks>
    /// No block below the activation has an access list, so the sync stops there instead of walking down to genesis.
    /// The activation is found by a binary search over canonical headers, which covers both block number and timestamp
    /// activations. When the pivot predates activation or a header in the search is missing, the configured barrier is kept.
    /// </remarks>
    private ulong ResolveBarrier(bool logFallback)
    {
        ulong barrier = SyncConfigBarrierCalc;
        BlockHeader? pivotHeader = FindPivotHeader(out ulong pivotNumber, out _);
        if (pivotHeader is null || barrier >= pivotNumber || !_specProvider.GetSpec(pivotHeader).BlockLevelAccessListsEnabled)
        {
            return barrier;
        }

        if (!TryGetBlockAccessListsEnabled(barrier, out bool enabled))
            return KeepConfiguredBarrier(barrier, barrier, logFallback);
        if (enabled) return barrier;

        ulong lastBeforeActivation = barrier;
        ulong firstAfterActivation = pivotNumber;
        while (firstAfterActivation - lastBeforeActivation > 1)
        {
            ulong middle = lastBeforeActivation + (firstAfterActivation - lastBeforeActivation) / 2;
            if (!TryGetBlockAccessListsEnabled(middle, out enabled))
                return KeepConfiguredBarrier(barrier, middle, logFallback);
            if (enabled) firstAfterActivation = middle;
            else lastBeforeActivation = middle;
        }

        return lastBeforeActivation;
    }

    private ulong KeepConfiguredBarrier(ulong barrier, ulong missingHeaderNumber, bool logFallback)
    {
        if (logFallback && !_barrierFallbackLogged && _logger.IsInfo)
        {
            _logger.Info($"Block access lists sync cannot find the EIP-7928 activation because header {missingHeaderNumber} is missing; keeping barrier {barrier}");
            _barrierFallbackLogged = true;
        }
        return barrier;
    }

    private bool TryGetBlockAccessListsEnabled(ulong blockNumber, out bool enabled)
    {
        enabled = false;
        if (_blockTree.FindCanonicalBlockInfo(blockNumber) is not { } blockInfo ||
            _blockTree.FindHeader(blockInfo.BlockHash, blockNumber: blockNumber) is not { } header)
            return false;
        enabled = _specProvider.GetSpec(header).BlockLevelAccessListsEnabled;
        return true;
    }

    private void ResetSyncStatusList() =>
        _syncStatusList = new SyncStatusList(
            _blockTree,
            _pivotNumber,
            _syncPointers.LowestInsertedBlockAccessListBlockNumber,
            _barrier);

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
        _syncReport.FastBlockAccessLists.Update(_pivotNumber - _barrier);
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
        int gaps = 0;
        ulong lowestGap = ulong.MaxValue;
        ulong highestGap = 0;
        RemoveSkippedUnavailable();

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
                    _unavailable.TryRemove(blockInfo.BlockNumber, out _);
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
            else if (blockInfo is not null)
            {
                if (hasAccessListResponse && IsUnavailableEverywhere(blockInfo.BlockNumber, batch.ResponseSourcePeer?.SyncPeer))
                {
                    _syncStatusList.MarkInserted(blockInfo.BlockNumber);
                    gaps++;
                    lowestGap = ulong.Min(lowestGap, blockInfo.BlockNumber);
                    highestGap = ulong.Max(highestGap, blockInfo.BlockNumber);
                }
                else
                {
                    _syncStatusList.MarkPending(blockInfo);
                }
            }
        }

        if (gaps > 0 && _logger.IsWarn)
            _logger.Warn($"Leaving out {gaps} block access lists between {lowestGap} and {highestGap}: unavailable from {MaxUnavailableResponses} peers.");

        UpdateSyncReport();
        LogPostProcessingBatchInfo(batch, validResponsesCount);
        return validResponsesCount;
    }

    /// <summary>Records which node reported the height unavailable and reports when enough different nodes have.</summary>
    /// <remarks>Keyed on the node ID, not the session, so a node that reconnects counts once.</remarks>
    private bool IsUnavailableEverywhere(ulong blockNumber, ISyncPeer? peer)
    {
        if (peer?.Node?.Id is not { } nodeId || IsInsideServingWindow(blockNumber)) return false;

        HashSet<PublicKey> peers = _unavailable.GetOrAdd(blockNumber, static _ => []);
        lock (peers)
        {
            peers.Add(nodeId);
            if (peers.Count < MaxUnavailableResponses) return false;
        }

        _unavailable.TryRemove(blockNumber, out _);
        return true;
    }

    /// <summary>Whether peers must still serve the height: within the chain's minimum BAL retention of the head.</summary>
    /// <remarks>The pivot stands in for the head during fast sync, when the head is still near genesis.</remarks>
    private bool IsInsideServingWindow(ulong blockNumber)
    {
        ulong head = ulong.Max(_blockTree.Head?.Number ?? 0, _blockTree.SyncPivot.BlockNumber);
        ulong windowBlocks = _historyPruner.GetRetentionBlocks(_specProvider.GenesisSpec.MinBalRetentionEpochs);
        return blockNumber >= head.SaturatingSub(windowBlocks);
    }

    /// <summary>Drops tracking for heights the feed no longer requests because the pruning cutoff has passed them.</summary>
    private void RemoveSkippedUnavailable()
    {
        if (_unavailable.IsEmpty) return;

        ulong skippedBelow = SkippedBelow;
        foreach (KeyValuePair<ulong, HashSet<PublicKey>> entry in _unavailable)
        {
            if (entry.Key < skippedBelow) _unavailable.TryRemove(entry.Key, out _);
        }
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

    private class BlockAccessListDownloadStrategy(IBlockTree blockTree, ISyncReport syncReport, IHistoryPruner historyPruner) : IBlockDownloadStrategy
    {
        private ulong _lowestQueriedBlockWithAccessLists = ulong.MaxValue;

        public bool ShouldDownloadBlock(BlockInfo info)
        {
            // The pruner has moved past these heights already, so an access list stored here would never be deleted.
            if (info.BlockNumber < ulong.Min(historyPruner.BalCutoffBlockNumber ?? 0, blockTree.SyncPivot.BlockNumber))
            {
                syncReport.FastBlockAccessLists.IncrementSkipped();
                return false;
            }

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
