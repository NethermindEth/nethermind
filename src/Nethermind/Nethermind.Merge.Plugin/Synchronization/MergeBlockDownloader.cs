//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
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
    public class MergeBlockDownloader : BlockDownloader
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
        private int _sinceLastTimeout;

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
            ILogManager logManager)
            : base(feed, syncPeerPool, blockTree, blockValidator, sealValidator, syncReport, receiptStorage,
                specProvider, new MergeBlocksSyncPeerAllocationStrategyFactory(posSwitcher, logManager),
                betterPeerStrategy, logManager)
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
            _logger = logManager.GetClassLogger();
        }

        public override async Task<long> DownloadBlocks(PeerInfo? bestPeer, BlocksRequest blocksRequest,
            CancellationToken cancellation)
        {
            if (_beaconPivot.BeaconPivotExists() == false && _poSSwitcher.HasEverReachedTerminalBlock() == false)
                return await base.DownloadBlocks(bestPeer, blocksRequest, cancellation);

            if (bestPeer == null)
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
            if (_logger.IsTrace)
                _logger.Trace(
                    $"MergeBlockDownloader GetCurrentNumber: currentNumber {currentNumber}, beaconPivotExists: {_beaconPivot.BeaconPivotExists()}, BestSuggestedBody: {_blockTree.BestSuggestedBody.Number}, BestKnownNumber: {_blockTree.BestKnownNumber}, BestPeer: {bestPeer}, BestKnownBeaconNumber {_blockTree.BestKnownBeaconNumber}");

            bool HasMoreToSync()
                => currentNumber < _blockTree.BestKnownBeaconNumber &&
                   bestPeer.HeadNumber > _blockTree.BestKnownNumber;

            while (HasMoreToSync())
            {
                if (_logger.IsDebug)
                    _logger.Debug($"Continue full sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

                long upperDownloadBoundary = _blockTree.BestKnownBeaconNumber;
                long blocksLeft = upperDownloadBoundary - currentNumber;
                int headersToRequest = (int)Math.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (headersToRequest <= 1)
                {
                    break;
                }

                headersToRequest = Math.Min(headersToRequest, bestPeer.MaxHeadersPerRequest());

                if (_logger.IsTrace)
                    _logger.Trace(
                        $"Full sync request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                Block[]? blocks = null;
                TxReceipt[]?[]? receipts = null;
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"Downloading blocks from peer. CurrentNumber: {currentNumber}, BeaconPivot: {_beaconPivot.PivotNumber}, BestPeer: {bestPeer}, HeaderToRequest: {headersToRequest}");
                
                BlockHeader[] headers = _chainLevelHelper.GetNextHeaders(headersToRequest);
                if (headers == null || headers.Length == 0)
                    break;
                BlockDownloadContext context = new(_specProvider, bestPeer, headers, downloadReceipts,
                    _receiptsRecovery);

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                await RequestBodies(bestPeer, cancellation, context);

                if (downloadReceipts)
                {
                    if (cancellation.IsCancellationRequested)
                        return blocksSynced; // check before every heavy operation
                    await RequestReceipts(bestPeer, cancellation, context);
                }

                _sinceLastTimeout++;
                if (_sinceLastTimeout > 2)
                {
                    _syncBatchSize.Expand();
                }

                blocks = context.Blocks;
                receipts = context.ReceiptsForBlocks;

                if (blocks == null || blocks.Length == 0)
                    break;

                for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                        break;
                    }

                    Block currentBlock = blocks[blockIndex];
                    if (_logger.IsTrace) _logger.Trace($"Received {currentBlock} from {bestPeer}");

                    // can move this to block tree now?
                    if (!_blockValidator.ValidateSuggestedBlock(currentBlock))
                    {
                        throw new EthSyncException(
                            $"{bestPeer} sent an invalid block {currentBlock.ToString(Block.Format.Short)}.");
                    }

                    if (downloadReceipts)
                    {
                        TxReceipt[]? contextReceiptsForBlock = receipts![blockIndex];
                        if (currentBlock.Header.HasBody && contextReceiptsForBlock == null)
                        {
                            throw new EthSyncException(
                                $"{bestPeer} didn't send receipts for block {currentBlock.ToString(Block.Format.Short)}.");
                        }
                    }

                    bool blockExists =
                        _blockTree.FindBlock(currentBlock.Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded) !=
                        null;
                    bool isKnownBlock = _blockTree.IsKnownBlock(currentBlock.Number, currentBlock.Hash) != null;
                    BlockTreeSuggestOptions suggestOptions =
                        shouldProcess ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None;
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"Current block {currentBlock}, BlockExists {blockExists} BeaconPivot: {_beaconPivot.PivotNumber}, IsKnownBlock: {isKnownBlock}");


                    if (blockExists == false && isKnownBlock)
                        _blockTree.Insert(currentBlock);
                    if (isKnownBlock && shouldProcess)
                        suggestOptions |= BlockTreeSuggestOptions.FillBeaconBlock;
                    
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"MergeBlockDownloader - SuggestBlock {currentBlock}, IsKnownBlock {isKnownBlock} ShouldProcess: {shouldProcess}");
                    if (HandleAddResult(bestPeer, currentBlock.Header, blockIndex == 0,
                            _blockTree.SuggestBlock(currentBlock, suggestOptions)))
                    {
                        if (shouldProcess == false)
                            _blockTree.UpdateMainChain(new[] { currentBlock }, false);
                        TryUpdateTerminalBlock(currentBlock.Header, shouldProcess);

                        if (downloadReceipts)
                        {
                            TxReceipt[]? contextReceiptsForBlock = receipts![blockIndex];
                            if (contextReceiptsForBlock != null)
                            {
                                _receiptStorage.Insert(currentBlock, contextReceiptsForBlock);
                            }
                            else
                            {
                                // this shouldn't now happen with new validation above, still lets keep this check 
                                if (currentBlock.Header.HasBody)
                                {
                                    if (_logger.IsError) _logger.Error($"{currentBlock} is missing receipts");
                                }
                            }
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
