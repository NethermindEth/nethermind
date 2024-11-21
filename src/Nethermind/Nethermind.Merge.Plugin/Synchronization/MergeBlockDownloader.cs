// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Merge.Plugin.Synchronization
{
    public class MergeBlockDownloader : BlockDownloader, ISyncDownloader<BlocksRequest>
    {
        private readonly IBeaconPivot _beaconPivot;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly ISyncReport _syncReport;
        private readonly IChainLevelHelper _chainLevelHelper;
        private readonly IPoSSwitcher _poSSwitcher;

        public MergeBlockDownloader(
            IPoSSwitcher posSwitcher,
            IBeaconPivot beaconPivot,
            ISyncFeed<BlocksRequest?>? feed,
            IBlockTree? blockTree,
            IBlockValidator? blockValidator,
            ISealValidator? sealValidator,
            ISyncReport? syncReport,
            IReceiptStorage? receiptStorage,
            ISpecProvider specProvider,
            IBetterPeerStrategy betterPeerStrategy,
            IChainLevelHelper chainLevelHelper,
            IFullStateFinder fullStateFinder,
            ILogManager logManager,
            SyncBatchSize? syncBatchSize = null)
            : base(feed, blockTree, blockValidator, sealValidator, syncReport, receiptStorage,
                specProvider, betterPeerStrategy, fullStateFinder, logManager, syncBatchSize)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _chainLevelHelper = chainLevelHelper ?? throw new ArgumentNullException(nameof(chainLevelHelper));
            _poSSwitcher = posSwitcher ?? throw new ArgumentNullException(nameof(posSwitcher));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _beaconPivot = beaconPivot;
            _logger = logManager.GetClassLogger();
        }

        public override async Task Dispatch(PeerInfo bestPeer, BlocksRequest? blocksRequest, CancellationToken cancellation)
        {
            if (_beaconPivot.BeaconPivotExists() == false && _poSSwitcher.HasEverReachedTerminalBlock() == false)
            {
                if (_logger.IsDebug)
                    _logger.Debug("Using pre merge dispatcher");
                await base.Dispatch(bestPeer, blocksRequest, cancellation);
                return;
            }

            if (blocksRequest is null)
            {
                if (_logger.IsWarn) _logger.Warn($"NULL received for dispatch in {nameof(BlockDownloader)}");
                return;
            }

            if (!_blockTree.CanAcceptNewBlocks) return;

            if (_previousBestPeer != bestPeer)
            {
                _syncBatchSize.Reset();
            }
            _previousBestPeer = bestPeer;

            try
            {
                InvokeEvent(new SyncEventArgs(bestPeer.SyncPeer, Nethermind.Synchronization.SyncEvent.Started));
                await DownloadBlocks(bestPeer, blocksRequest, cancellation)
                        .ContinueWith(t => HandleSyncRequestResult(t, bestPeer), cancellation);
            }
            finally
            {
                _allocationWithCancellation.Dispose();
            }
        }

        IReadOnlyList<BlockHeader>? HasMoreToSync(PeerInfo bestPeer, BlocksRequest blocksRequest, long currentNumber, CancellationToken cancellation)
        {
            if (_logger.IsDebug)
                _logger.Debug($"Continue full sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

            int headersToRequest = Math.Min(_syncBatchSize.Current, bestPeer.MaxHeadersPerRequest());
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Full sync request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");

            IReadOnlyList<BlockHeader>? headers = _chainLevelHelper.GetNextHeaders(headersToRequest, bestPeer.HeadNumber, blocksRequest.NumberOfLatestBlocksToBeIgnored ?? 0);
            if (headers is null || headers.Count <= 1)
            {
                if (_logger.IsTrace)
                    _logger.Trace("Chain level helper got no headers suggestion");
                return null;
            }

            // Alternatively we can do this in BeaconHeadersSyncFeed, but this seems easier.
            ValidateSeals(headers!, cancellation);

            if (HasBetterPeer)
            {
                if (_logger.IsDebug) _logger.Debug("Has better peer, stopping");
                return null;
            }
            if (_logger.IsTrace) _logger.Trace($"Downloading blocks from peer. CurrentNumber: {currentNumber}, BeaconPivot: {_beaconPivot.PivotNumber}, BestPeer: {bestPeer}");

            return headers;
        }


        public override async Task<long> DownloadBlocks(PeerInfo? bestPeer, BlocksRequest blocksRequest,
            CancellationToken cancellation)
        {
            if (_beaconPivot.BeaconPivotExists() == false && _poSSwitcher.HasEverReachedTerminalBlock() == false)
            {
                if (_logger.IsDebug)
                    _logger.Debug("Using pre merge block downloader");
                return await base.DownloadBlocks(bestPeer, blocksRequest, cancellation);
            }

            if (bestPeer is null)
            {
                string message = $"Not expecting best peer to be null inside the {nameof(BlockDownloader)}";
                if (_logger.IsError) _logger.Error(message);
                throw new ArgumentNullException(message);
            }

            DownloaderOptions options = blocksRequest.Options;
            bool shouldProcess = (options & DownloaderOptions.Full) == DownloaderOptions.Full;

            int blocksSynced = 0;
            long currentNumber = _blockTree.BestKnownNumber;
            if (_logger.IsDebug)
                _logger.Debug(
                    $"MergeBlockDownloader GetCurrentNumber: currentNumber {currentNumber}, beaconPivotExists: {_beaconPivot.BeaconPivotExists()}, BestSuggestedBody: {_blockTree.BestSuggestedBody?.Number}, BestKnownNumber: {_blockTree.BestKnownNumber}, BestPeer: {bestPeer}, BestKnownBeaconNumber {_blockTree.BestKnownBeaconNumber}");

            long bestProcessedBlock = 0;
            while (HasMoreToSync(bestPeer, blocksRequest, currentNumber, cancellation) is {} headers)
            {
                if (cancellation.IsCancellationRequested) break; // check before every heavy operation

                bool downloadReceipts = !shouldProcess;
                BlockDownloadContext context = await DoDownload(bestPeer, headers!, downloadReceipts, cancellation);
                headers.TryDispose();
                if (cancellation.IsCancellationRequested) break; // check before every heavy operation

                Block[]? blocks = context.Blocks;;
                TxReceipt[]?[]? receipts = context.ReceiptsForBlocks;;

                if (!(blocks?.Length > 0))
                {
                    if (_logger.IsTrace) _logger.Trace("Break early due to no blocks.");
                    break;
                }

                for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                        break;
                    }

                    Block currentBlock = blocks[blockIndex];
                    ValidateBlock(bestPeer, currentBlock);
                    (shouldProcess, downloadReceipts, receipts) = await HandleReceiptEdgeCase(bestPeer, cancellation, currentBlock, bestProcessedBlock, context, shouldProcess, downloadReceipts, receipts);
                    ValidateReceiptExistance(bestPeer, downloadReceipts, context, blockIndex, currentBlock);

                    BlockTreeSuggestOptions suggestOptions =
                        shouldProcess ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None;

                    bool isKnownBeaconBlock = _blockTree.IsKnownBeaconBlock(currentBlock.Number, currentBlock.GetOrCalculateHash());
                    if (isKnownBeaconBlock)
                    {
                        suggestOptions |= BlockTreeSuggestOptions.FillBeaconBlock;
                    }

                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"MergeBlockDownloader - SuggestBlock {currentBlock}, IsKnownBeaconBlock {isKnownBeaconBlock} ShouldProcess: {shouldProcess}, IsKnownBeaconBlock: {isKnownBeaconBlock}");

                    AddBlockResult addResult = AddBlock(bestPeer, currentBlock, suggestOptions, blockIndex, downloadReceipts, receipts, ref bestProcessedBlock, ref blocksSynced);

                    currentNumber += 1;

                    if (addResult == AddBlockResult.Added)
                    {
                        if ((_beaconPivot.ProcessDestination?.Number ?? long.MaxValue) < currentBlock.Number)
                        {
                            // Move the process destination in front, otherwise `ChainLevelHelper` would continue returning
                            // already processed header starting from `ProcessDestination`.
                            _beaconPivot.ProcessDestination = currentBlock.Header;
                        }
                    }
                }

                if (blocksSynced > 0)
                {
                    _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0);
                    _syncReport.FullSyncBlocksKnown = bestPeer.HeadNumber;
                }
                else
                {
                    break;
                }
            }

            return blocksSynced;
        }

        protected override async Task<IOwnedReadOnlyList<BlockHeader>> RequestHeaders(PeerInfo peer, CancellationToken cancellation, long currentNumber, int headersToRequest)
        {
            // Override PoW's RequestHeaders so that it won't request beyond PoW.
            // This fixes `Incremental Sync` hive test.
            IOwnedReadOnlyList<BlockHeader> response = await base.RequestHeaders(peer, cancellation, currentNumber, headersToRequest);
            if (response.Count > 0)
            {
                BlockHeader lastBlockHeader = response[^1];
                bool lastBlockIsPostMerge = _poSSwitcher.GetBlockConsensusInfo(response[^1]).IsPostMerge;
                if (lastBlockIsPostMerge) // Initial check to prevent creating new array every time
                {
                    response = response
                        .TakeWhile(header => !_poSSwitcher.GetBlockConsensusInfo(header).IsPostMerge)
                        .ToPooledList(response.Count);
                    if (_logger.IsInfo) _logger.Info($"Last block is post merge. {lastBlockHeader.Hash}. Trimming to {response.Count} sized batch.");
                }
            }
            return response;
        }

        protected override void TryUpdateTerminalBlock(BlockHeader header, bool shouldProcess)
        {
            if (shouldProcess == false) // if we're processing the block we will find TerminalBlock after processing
                _poSSwitcher.TryUpdateTerminalBlock(header);
        }

        protected override bool ImprovementRequirementSatisfied(PeerInfo? bestPeer)
        {
            return bestPeer!.TotalDifficulty > (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0) &&
                   _poSSwitcher.HasEverReachedTerminalBlock() == false;
        }
    }
}
