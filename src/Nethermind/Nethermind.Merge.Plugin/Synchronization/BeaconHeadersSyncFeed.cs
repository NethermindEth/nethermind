// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Merge.Plugin.Synchronization;

public sealed class BeaconHeadersSyncFeed : HeadersSyncFeed
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IInvalidChainTracker _invalidChainTracker;
    private readonly IPivot _pivot;
    private readonly IMergeConfig _mergeConfig;
    private readonly ILogger _logger;
    private bool _chainMerged;

    protected override long HeadersDestinationNumber => _pivot.PivotDestinationNumber;

    protected override bool AllHeadersDownloaded => (_blockTree.LowestInsertedBeaconHeader?.Number ?? long.MaxValue) <=
                                                    _pivot.PivotDestinationNumber || _chainMerged;

    protected override BlockHeader? LowestInsertedBlockHeader => _blockTree.LowestInsertedBeaconHeader;
    protected override MeasuredProgress HeadersSyncProgressReport => _syncReport.BeaconHeaders;

    protected override MeasuredProgress HeadersSyncQueueReport => _syncReport.BeaconHeadersInQueue;

    public BeaconHeadersSyncFeed(
        IPoSSwitcher poSSwitcher,
        ISyncModeSelector syncModeSelector,
        IBlockTree? blockTree,
        ISyncPeerPool? syncPeerPool,
        ISyncConfig? syncConfig,
        ISyncReport? syncReport,
        IPivot? pivot,
        IMergeConfig? mergeConfig,
        IInvalidChainTracker invalidChainTracker,
        ILogManager logManager)
        : base(syncModeSelector, blockTree, syncPeerPool, syncConfig, syncReport, logManager,
            true) // alwaysStartHeaderSync = true => for the merge we're forcing header sync start. It doesn't matter if it is archive sync or fast sync
    {
        _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        _pivot = pivot ?? throw new ArgumentNullException(nameof(pivot));
        _mergeConfig = mergeConfig ?? throw new ArgumentNullException(nameof(mergeConfig));
        _invalidChainTracker = invalidChainTracker;
        _logger = logManager.GetClassLogger();
    }

    protected override SyncMode ActivationSyncModes { get; }
        = SyncMode.BeaconHeaders;

    public override bool IsMultiFeed => true;

    public override AllocationContexts Contexts => AllocationContexts.Headers;

    private long ExpectedPivotNumber =>
        _pivot.PivotParentHash is not null ? _pivot.PivotNumber - 1 : _pivot.PivotNumber;

    private Keccak ExpectedPivotHash => _pivot.PivotParentHash ?? _pivot.PivotHash ?? Keccak.Zero;

    protected override void ResetPivot()
    {
        _chainMerged = false;

        // First, we assume pivot
        _pivotNumber = ExpectedPivotNumber;
        _nextHeaderHash = ExpectedPivotHash;
        _nextHeaderDiff = _poSSwitcher.FinalTotalDifficulty;

        long startNumber = _pivotNumber;

        // In case we already have beacon sync happened before
        BlockHeader? lowestInserted = LowestInsertedBlockHeader;
        if (lowestInserted is not null && lowestInserted.Number <= _pivotNumber)
        {
            startNumber = lowestInserted.Number - 1;
            _nextHeaderHash = lowestInserted.ParentHash ?? Keccak.Zero;
            _nextHeaderDiff = lowestInserted.TotalDifficulty - lowestInserted.Difficulty;
        }

        // the base class with starts with _lowestRequestedHeaderNumber - 1, so we offset it here.
        _lowestRequestedHeaderNumber = startNumber + 1;

        _logger.Info($"Initialized beacon headers sync. lowestRequestedHeaderNumber: {_lowestRequestedHeaderNumber}," +
                     $"lowestInsertedBlockHeader: {lowestInserted?.ToString(BlockHeader.Format.FullHashAndNumber)}, pivotNumber: {_pivotNumber}, pivotDestination: {_pivot.PivotDestinationNumber}");
    }

    protected override void FinishAndCleanUp()
    {
        // make feed dormant as there may be more header syncs when there is a new beacon pivot
        FallAsleep();
        PostFinishCleanUp();
    }

    protected override void PostFinishCleanUp()
    {
        HeadersSyncProgressReport.Update(_pivotNumber - HeadersDestinationNumber + 1);
        HeadersSyncProgressReport.MarkEnd();
        ClearDependencies(); // there may be some dependencies from wrong branches
        _pending.Clear(); // there may be pending wrong branches
        _sent.Clear(); // we my still be waiting for some bad branches
        HeadersSyncQueueReport.Update(0L);
        HeadersSyncQueueReport.MarkEnd();
    }

    public override Task<HeadersSyncBatch?> PrepareRequest(CancellationToken cancellationToken = default)
    {
        if (_pivotNumber != ExpectedPivotNumber)
        {
            // Pivot changed during the sync. Need to reset the states
            InitializeFeed();
        }

        return base.PrepareRequest(cancellationToken);
    }

    protected override int InsertHeaders(HeadersSyncBatch batch)
    {
        if (batch.Response is not null)
        {
            ConnectHeaderChainInInvalidChainTracker(batch.Response);
        }

        return base.InsertHeaders(batch);
    }

    private void ConnectHeaderChainInInvalidChainTracker(IReadOnlyList<BlockHeader?> batchResponse)
    {
        // Sometimes multiple consecutive block failed validation, but engine api need to know the earliest valid hash.
        // Currently, HeadersSyncFeed insert header in reverse and break early on invalid header meaning header
        // chain is not connected, so earlier invalid block in chain is not checked and reported even if later request
        // get to it. So we are trying to connect them first. Note: This does not completely fix the issue.
        for (int i = 0; i < batchResponse.Count; i++)
        {
            BlockHeader? header = batchResponse[i];
            if (header is not null && HeaderValidator.ValidateHash(header))
            {
                _invalidChainTracker.SetChildParent(header.Hash!, header.ParentHash!);
            }
        }
    }

    protected override AddBlockResult InsertToBlockTree(BlockHeader header)
    {
        if (_chainMerged)
        {
            if (_logger.IsTrace)
                _logger.Trace(
                    "Chain already merged, skipping header insert");
            return AddBlockResult.AlreadyKnown;
        }

        if (_logger.IsTrace)
            _logger.Trace(
                $"Adding new header in beacon headers sync {header.ToString(BlockHeader.Format.FullHashAndNumber)}");
        BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconHeaderInsert;
        if (_nextHeaderDiff is null)
        {
            headerOptions |= BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded;
        }

        // Found existing block in the block tree
        if (!_syncConfig.StrictMode && _blockTree.IsKnownBlock(header.Number, header.GetOrCalculateHash()))
        {
            _chainMerged = true;
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Found header to join dangling beacon chain {header.ToString(BlockHeader.Format.FullHashAndNumber)}");
            return AddBlockResult.AlreadyKnown;
        }

        AddBlockResult insertOutcome = _blockTree.Insert(header, headerOptions);

        if (insertOutcome == AddBlockResult.Added || insertOutcome == AddBlockResult.AlreadyKnown)
        {
            _nextHeaderHash = header.ParentHash!;
            if (_expectedDifficultyOverride?.TryGetValue(header.Number, out ulong nextHeaderDiff) == true)
            {
                _nextHeaderDiff = nextHeaderDiff;
            }
            else
            {
                _nextHeaderDiff = header.TotalDifficulty is not null && header.TotalDifficulty >= header.Difficulty
                    ? header.TotalDifficulty - header.Difficulty
                    : null;
            }
        }

        if (_logger.IsTrace)
            _logger.Trace(
                $"New header {header.ToString(BlockHeader.Format.FullHashAndNumber)} in beacon headers sync. InsertOutcome: {insertOutcome}");
        return insertOutcome;
    }
}
