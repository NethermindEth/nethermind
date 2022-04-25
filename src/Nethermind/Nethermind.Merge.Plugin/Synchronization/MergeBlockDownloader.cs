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
using System.Collections.Generic;
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
            : base(feed, syncPeerPool, blockTree, blockValidator, sealValidator, syncReport, receiptStorage, specProvider, new MergeBlocksSyncPeerAllocationStrategyFactory(posSwitcher, logManager), betterPeerStrategy, logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _chainLevelHelper = chainLevelHelper ?? throw new ArgumentNullException(nameof(chainLevelHelper));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _beaconPivot = beaconPivot;
            _receiptsRecovery = new ReceiptsRecovery(new EthereumEcdsa(specProvider.ChainId, logManager), specProvider);
            _logger = logManager.GetClassLogger();
        }
        
        protected override long GetCurrentNumber(PeerInfo bestPeer)
        {
            long currentNumber = _beaconPivot.BeaconPivotExists()
                ? Math.Max(0, Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1))
                : base.GetCurrentNumber(bestPeer);
            if (_logger.IsTrace) _logger.Trace($"MergeBlockDownloader GetCurrentNumber: currentNumber {currentNumber}, beaconPivotExists: {_beaconPivot.BeaconPivotExists()}, BestSuggestedBody: {_blockTree.BestSuggestedBody.Number}, BestKnownNumber: {_blockTree.BestKnownNumber}");
            return currentNumber;
        }
        
        protected override long GetUpperDownloadBoundary(PeerInfo bestPeer, BlocksRequest blocksRequest)
        {
            long preMergeUpperDownloadBoundary = base.GetUpperDownloadBoundary(bestPeer, blocksRequest);
            long upperDownloadBoundary = _beaconPivot.BeaconPivotExists()
                ? bestPeer.HeadNumber
                : preMergeUpperDownloadBoundary;
            if (_logger.IsTrace) _logger.Trace($"MergeBlockDownloader GetUpperDownloadBoundary: {upperDownloadBoundary}, beaconPivotExists: {_beaconPivot.BeaconPivotExists()}, BestSuggestedBody: {_blockTree.BestSuggestedBody.Number}, BestKnownNumber {_blockTree.BestKnownNumber}, PreMergeUpperDownloadBoundary: {preMergeUpperDownloadBoundary}");
            return upperDownloadBoundary;
        }

        protected override bool ImprovementRequirementSatisfied(PeerInfo? bestPeer)
        {
            bool preMergeDifficultyRequirementSatisfied = base.ImprovementRequirementSatisfied(bestPeer);
            bool postMergeRequirementSatisfied = _beaconPivot.BeaconPivotExists() 
                                                 && bestPeer!.HeadNumber > (_blockTree.BestSuggestedBody?.Number ?? 0);
            bool improvementRequirementSatisfied = _beaconPivot.BeaconPivotExists()
                ? postMergeRequirementSatisfied
                : preMergeDifficultyRequirementSatisfied;
            
            if (_logger.IsTrace) _logger.Trace($"MergeBlockDownloader ImprovementRequirementSatisfied: {improvementRequirementSatisfied}, beaconPivotExists: {_beaconPivot.BeaconPivotExists()}, BestSuggestedBody: {_blockTree.BestSuggestedBody.Number}, BestPeer: {bestPeer!.HeadNumber}, BestKnownNumber {_blockTree.BestKnownNumber} BeaconPivot: {_beaconPivot.PivotNumber}");
            return improvementRequirementSatisfied;
        }
        
        public override async Task<long> DownloadBlocks(PeerInfo? bestPeer, BlocksRequest blocksRequest,
            CancellationToken cancellation)
        {
            if (_beaconPivot.BeaconPivotExists() == false)
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
            int ancestorLookupLevel = 0;

            long currentNumber = GetCurrentNumber(bestPeer);
            // pivot number - 6 for uncle validation
            // long currentNumber = Math.Max(Math.Max(0, pivotNumber - 6), Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1));


        bool HasMoreToSync()
                => currentNumber <= bestPeer!.HeadNumber;
            while(ImprovementRequirementSatisfied(bestPeer!) && HasMoreToSync())
            {
                if (_logger.IsDebug) _logger.Debug($"Continue full sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");
                
                long upperDownloadBoundary = GetUpperDownloadBoundary(bestPeer, blocksRequest);
                long blocksLeft = upperDownloadBoundary - currentNumber;
                int headersToRequest = (int) Math.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (headersToRequest <= 1)
                {
                    break;
                }

                headersToRequest = Math.Min(headersToRequest, bestPeer.MaxHeadersPerRequest());
                if (_logger.IsTrace) _logger.Trace($"Full sync request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                BlockHeader[] headers = _chainLevelHelper.GetNextHeaders(headersToRequest);
                BlockDownloadContext context = new(_specProvider, bestPeer, headers, downloadReceipts, _receiptsRecovery);

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                await RequestBodies(bestPeer, cancellation, context);

                if (downloadReceipts)
                {
                    if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                    await RequestReceipts(bestPeer, cancellation, context);
                }

                _sinceLastTimeout++;
                if (_sinceLastTimeout > 2)
                {
                    _syncBatchSize.Expand();
                }

                Block[] blocks = context.Blocks;
                Block blockZero = blocks[0];
                if (context.FullBlocksCount > 0)
                {
                    bool parentIsKnown = _blockTree.IsKnownBlock(blockZero.Number - 1, blockZero.ParentHash);
                    if (!parentIsKnown)
                    {
                        ancestorLookupLevel++;
                        if (ancestorLookupLevel >= _ancestorJumps.Length)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer}");
                            throw new EthSyncException("Peer with inconsistent chain in sync");
                        }

                        int ancestorJump = _ancestorJumps[ancestorLookupLevel] - _ancestorJumps[ancestorLookupLevel - 1];
                        currentNumber = currentNumber >= ancestorJump ? (currentNumber - ancestorJump) : 0L;
                        continue;
                    }
                }

                ancestorLookupLevel = 0;
                for (int blockIndex = 0; blockIndex < context.FullBlocksCount; blockIndex++)
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
                        throw new EthSyncException($"{bestPeer} sent an invalid block {currentBlock.ToString(Block.Format.Short)}.");
                    }

                    if (downloadReceipts)
                    {
                        TxReceipt[]? contextReceiptsForBlock = context.ReceiptsForBlocks![blockIndex];
                        if (currentBlock.Header.HasBody && contextReceiptsForBlock == null)
                        {
                            throw new EthSyncException($"{bestPeer} didn't send receipts for block {currentBlock.ToString(Block.Format.Short)}.");
                        }
                    }

                    bool blockExists = _blockTree.FindBlock(currentBlock.Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded) != null;
                    bool isKnownBlock = _blockTree.IsKnownBlock(currentBlock.Number, currentBlock.Hash) != null;
                    bool isOnMainChain = true;
                    BlockTreeSuggestOptions suggestOptions = shouldProcess ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None;
                    if (_logger.IsTrace) _logger.Trace($"Current block {currentBlock}, BlockExists {blockExists} IsOnMainChain: {isOnMainChain} BeaconPivot: {_beaconPivot.PivotNumber}, IsKnownBlock: {isKnownBlock}");
                    if (blockExists && isOnMainChain == false)
                    {
                        currentNumber += 1;
                        continue;
                    }

                    if (blockExists == false && isKnownBlock)
                        _blockTree.Insert(currentBlock);
                    if (isOnMainChain && isKnownBlock && shouldProcess)
                        suggestOptions |= BlockTreeSuggestOptions.TryProcessKnownBlock;

                    if (HandleAddResult(bestPeer, currentBlock.Header, blockIndex == 0, _blockTree.SuggestBlock(currentBlock, suggestOptions)))
                    {
                        if (downloadReceipts)
                        {
                            TxReceipt[]? contextReceiptsForBlock = context.ReceiptsForBlocks![blockIndex];
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
                        _blockTree.UpdateMainChain(new[] {currentBlock}, false);
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
    }
}
