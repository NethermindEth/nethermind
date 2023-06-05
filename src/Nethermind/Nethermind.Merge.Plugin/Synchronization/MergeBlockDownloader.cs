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
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
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
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly IBlockValidator _blockValidator;
        private readonly ISpecProvider _specProvider;
        private readonly ISyncReport _syncReport;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IChainLevelHelper _chainLevelHelper;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly ISyncProgressResolver _syncProgressResolver;

        public MergeBlockDownloader(
            IPoSSwitcher posSwitcher,
            IBeaconPivot beaconPivot,
            ISyncFeed<BlocksRequest?>? feed,
            ISyncPeerPool? syncPeerPool,
            IBlockTree? blockTree,
            IBlockValidator? blockValidator,
            ISealValidator? sealValidator,
            ISyncReport? syncReport,
            IReceiptStorage? receiptStorage,
            ISpecProvider specProvider,
            IBetterPeerStrategy betterPeerStrategy,
            IChainLevelHelper chainLevelHelper,
            ISyncProgressResolver syncProgressResolver,
            ILogManager logManager,
            SyncBatchSize? syncBatchSize = null)
            : base(feed, syncPeerPool, blockTree, blockValidator, sealValidator, syncReport, receiptStorage,
                specProvider,
                betterPeerStrategy, logManager, syncBatchSize)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _chainLevelHelper = chainLevelHelper ?? throw new ArgumentNullException(nameof(chainLevelHelper));
            _poSSwitcher = posSwitcher ?? throw new ArgumentNullException(nameof(posSwitcher));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _beaconPivot = beaconPivot;
            _receiptsRecovery = new ReceiptsRecovery(new EthereumEcdsa(specProvider.ChainId, logManager), specProvider);
            _syncProgressResolver = syncProgressResolver ?? throw new ArgumentNullException(nameof(syncProgressResolver));
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

            if (blocksRequest == null)
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
            bool downloadReceipts = (options & DownloaderOptions.WithReceipts) == DownloaderOptions.WithReceipts;
            bool shouldProcess = (options & DownloaderOptions.Process) == DownloaderOptions.Process;
            bool shouldMoveToMain = (options & DownloaderOptions.MoveToMain) == DownloaderOptions.MoveToMain;

            int blocksSynced = 0;
            long currentNumber = _blockTree.BestKnownNumber;
            if (_logger.IsDebug)
                _logger.Debug(
                    $"MergeBlockDownloader GetCurrentNumber: currentNumber {currentNumber}, beaconPivotExists: {_beaconPivot.BeaconPivotExists()}, BestSuggestedBody: {_blockTree.BestSuggestedBody?.Number}, BestKnownNumber: {_blockTree.BestKnownNumber}, BestPeer: {bestPeer}, BestKnownBeaconNumber {_blockTree.BestKnownBeaconNumber}");

            bool HasMoreToSync(out BlockHeader[]? headers, out int headersToRequest)
            {
                if (_logger.IsDebug)
                    _logger.Debug($"Continue full sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

                headersToRequest = Math.Min(_syncBatchSize.Current, bestPeer.MaxHeadersPerRequest());
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"Full sync request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");

                headers = _chainLevelHelper.GetNextHeaders(headersToRequest, bestPeer.HeadNumber, blocksRequest.NumberOfLatestBlocksToBeIgnored ?? 0);
                if (headers is null || headers.Length <= 1)
                {
                    if (_logger.IsTrace)
                        _logger.Trace("Chain level helper got no headers suggestion");
                    return false;
                }

                return true;
            }

            while (HasMoreToSync(out BlockHeader[]? headers, out int headersToRequest))
            {
                if (HasBetterPeer)
                {
                    if (_logger.IsDebug) _logger.Debug("Has better peer, stopping");
                    break;
                }

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                Block[]? blocks = null;
                TxReceipt[]?[]? receipts = null;
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"Downloading blocks from peer. CurrentNumber: {currentNumber}, BeaconPivot: {_beaconPivot.PivotNumber}, BestPeer: {bestPeer}, HeaderToRequest: {headersToRequest}");

                // Alternatively we can do this in BeaconHeadersSyncFeed, but this seems easier.
                ValidateSeals(headers!, cancellation);

                BlockDownloadContext context = new(_specProvider, bestPeer, headers!, downloadReceipts, _receiptsRecovery);

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation

                Stopwatch sw = Stopwatch.StartNew();
                await RequestBodies(bestPeer, cancellation, context);

                if (downloadReceipts)
                {
                    if (cancellation.IsCancellationRequested)
                        return blocksSynced; // check before every heavy operation
                    await RequestReceipts(bestPeer, cancellation, context);
                }

                AdjustSyncBatchSize(sw.Elapsed);

                blocks = context.Blocks;
                receipts = context.ReceiptsForBlocks;

                if (!(blocks?.Length > 0))
                {
                    if (_logger.IsTrace)
                        _logger.Trace("Break early due to no blocks.");
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
                    if (_logger.IsTrace) _logger.Trace($"Received {currentBlock} from {bestPeer}");

                    if (currentBlock.IsBodyMissing)
                    {
                        throw new EthSyncException($"{bestPeer} didn't send body for block {currentBlock.ToString(Block.Format.Short)}.");
                    }

                    // can move this to block tree now?
                    if (!_blockValidator.ValidateSuggestedBlock(currentBlock))
                    {
                        string message = $"{bestPeer} sent an invalid block {currentBlock.ToString(Block.Format.Short)}.";
                        if (_logger.IsWarn) _logger.Warn(message);
                        throw new EthSyncException(message);
                    }

                    if (shouldProcess)
                    {
                        // covering edge case during fastSyncTransition when we're trying to SuggestBlock without the state
                        bool headIsGenesis = _blockTree.Head?.IsGenesis ?? false;
                        bool toBeProcessedIsNotBlockOne = currentBlock.Number > 1;
                        bool isFastSyncTransition = headIsGenesis && toBeProcessedIsNotBlockOne;
                        if (isFastSyncTransition)
                        {
                            long bestFullState = _syncProgressResolver.FindBestFullState();
                            shouldProcess = currentBlock.Number > bestFullState && bestFullState != 0;
                            if (!shouldProcess)
                            {
                                if (_logger.IsInfo) _logger.Info($"Skipping processing during fastSyncTransition, currentBlock: {currentBlock}, bestFullState: {bestFullState}");
                                downloadReceipts = true;
                            }
                        }
                    }

                    if (downloadReceipts)
                    {
                        TxReceipt[]? contextReceiptsForBlock = receipts![blockIndex];
                        if (currentBlock.Header.HasTransactions && contextReceiptsForBlock is null)
                        {
                            throw new EthSyncException($"{bestPeer} didn't send receipts for block {currentBlock.ToString(Block.Format.Short)}.");
                        }
                    }

                    bool isKnownBeaconBlock = _blockTree.IsKnownBeaconBlock(currentBlock.Number, currentBlock.GetOrCalculateHash());
                    BlockTreeSuggestOptions suggestOptions =
                        shouldProcess ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None;
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"Current block {currentBlock}, BeaconPivot: {_beaconPivot.PivotNumber}, IsKnownBeaconBlock: {isKnownBeaconBlock}");

                    if (isKnownBeaconBlock)
                    {
                        suggestOptions |= BlockTreeSuggestOptions.FillBeaconBlock;
                    }

                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"MergeBlockDownloader - SuggestBlock {currentBlock}, IsKnownBeaconBlock {isKnownBeaconBlock} ShouldProcess: {shouldProcess}");

                    AddBlockResult addResult = _blockTree.SuggestBlock(currentBlock, suggestOptions);
                    if (HandleAddResult(bestPeer, currentBlock.Header, blockIndex == 0, addResult))
                    {
                        if (shouldProcess == false)
                        {
                            _blockTree.UpdateMainChain(new[] { currentBlock }, false);
                        }

                        TryUpdateTerminalBlock(currentBlock.Header, shouldProcess);

                        if (downloadReceipts)
                        {
                            TxReceipt[]? contextReceiptsForBlock = receipts![blockIndex];
                            if (contextReceiptsForBlock is not null)
                            {
                                _receiptStorage.Insert(currentBlock, contextReceiptsForBlock);
                            }
                            else
                            {
                                // this shouldn't now happen with new validation above, still lets keep this check
                                if (currentBlock.Header.HasTransactions)
                                {
                                    if (_logger.IsError) _logger.Error($"{currentBlock} is missing receipts");
                                }
                            }
                        }

                        if ((_beaconPivot.ProcessDestination?.Number ?? long.MaxValue) < currentBlock.Number)
                        {
                            // Move the process destination in front, otherwise `ChainLevelHelper` would continue returning
                            // already processed header starting from `ProcessDestination`.
                            _beaconPivot.ProcessDestination = currentBlock.Header;
                        }
                        blocksSynced++;
                    }

                    if (shouldMoveToMain)
                    {
                        _blockTree.UpdateMainChain(new[] { currentBlock }, false);
                    }

                    currentNumber += 1;
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

        protected override async Task<BlockHeader[]> RequestHeaders(PeerInfo peer, CancellationToken cancellation, long currentNumber, int headersToRequest)
        {
            // Override PoW's RequestHeaders so that it won't request beyond PoW.
            // This fixes `Incremental Sync` hive test.
            BlockHeader[] response = await base.RequestHeaders(peer, cancellation, currentNumber, headersToRequest);
            if (response.Length > 0)
            {
                BlockHeader lastBlockHeader = response[^1];
                bool lastBlockIsPostMerge = _poSSwitcher.GetBlockConsensusInfo(response[^1]).IsPostMerge;
                if (lastBlockIsPostMerge) // Initial check to prevent creating new array every time
                {
                    response = response
                        .TakeWhile((header) => !_poSSwitcher.GetBlockConsensusInfo(header).IsPostMerge)
                        .ToArray();
                    if (_logger.IsInfo) _logger.Info($"Last block is post merge. {lastBlockHeader.Hash}. Trimming to {response.Length} sized batch.");
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
