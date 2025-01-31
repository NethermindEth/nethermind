// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
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
        private readonly IChainLevelHelper _chainLevelHelper;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly BlockDownloader _preMergeBlockDownloader;

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
            IFullStateFinder fullStateFinder,
            IPosTransitionHook posTransitionHook,
            BlockDownloader preMergeBlockDownloader,
            ILogManager logManager,
            SyncBatchSize? syncBatchSize = null)
            : base(feed, syncPeerPool, blockTree, blockValidator, sealValidator, syncReport, receiptStorage,
                specProvider, betterPeerStrategy, fullStateFinder, posTransitionHook, logManager, syncBatchSize)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _chainLevelHelper = chainLevelHelper ?? throw new ArgumentNullException(nameof(chainLevelHelper));
            _poSSwitcher = posSwitcher ?? throw new ArgumentNullException(nameof(posSwitcher));
            _preMergeBlockDownloader = preMergeBlockDownloader;
            _beaconPivot = beaconPivot;
            _logger = logManager.GetClassLogger();
        }

        public override async Task Dispatch(PeerInfo bestPeer, BlocksRequest? blocksRequest, CancellationToken cancellation)
        {
            if (_beaconPivot.BeaconPivotExists() == false && _poSSwitcher.HasEverReachedTerminalBlock() == false)
            {
                if (_logger.IsDebug)
                    _logger.Debug("Using pre merge dispatcher");
                await _preMergeBlockDownloader.Dispatch(bestPeer, blocksRequest, cancellation);
                return;
            }

            await base.Dispatch(bestPeer, blocksRequest, cancellation);
        }

        public override async Task<long> DownloadBlocks(PeerInfo? bestPeer, BlocksRequest blocksRequest,
            CancellationToken cancellation)
        {
            // Note: Redundant with Dispatch, but test uses it.
            if (_beaconPivot.BeaconPivotExists() == false && _poSSwitcher.HasEverReachedTerminalBlock() == false)
            {
                if (_logger.IsDebug)
                    _logger.Debug("Using pre merge block downloader");
                return await _preMergeBlockDownloader.DownloadBlocks(bestPeer, blocksRequest, cancellation);
            }

            return await base.DownloadBlocks(bestPeer, blocksRequest, cancellation);
        }

        public override async Task<long> DownloadHeaders(PeerInfo? bestPeer, BlocksRequest blocksRequest,
            CancellationToken cancellation)
        {
            // Note: Redundant with Dispatch, but test uses it.
            if (_beaconPivot.BeaconPivotExists() == false && _poSSwitcher.HasEverReachedTerminalBlock() == false)
            {
                if (_logger.IsDebug)
                    _logger.Debug("Using pre merge block downloader");
                return await _preMergeBlockDownloader.DownloadHeaders(bestPeer, blocksRequest, cancellation);
            }

            return await base.DownloadHeaders(bestPeer, blocksRequest, cancellation);
        }

        protected override Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(PeerInfo bestPeer, long currentNumber, BlocksRequest blocksRequest, CancellationToken cancellation)
        {
            if (_logger.IsDebug)
                _logger.Debug($"Continue full sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

            int headersToRequest = Math.Min(_syncBatchSize.Current, bestPeer.MaxHeadersPerRequest());
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Full sync request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");

            BlockHeader?[]? headers = _chainLevelHelper.GetNextHeaders(headersToRequest, bestPeer.HeadNumber, blocksRequest.NumberOfLatestBlocksToBeIgnored ?? 0);
            if (headers is null || headers.Length <= 1)
            {
                if (_logger.IsTrace)
                    _logger.Trace("Chain level helper got no headers suggestion");
                return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(null);
            }

            // Alternatively we can do this in BeaconHeadersSyncFeed, but this seems easier.
            ValidateSeals(headers!, cancellation);

            return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(headers.ToPooledList(0));
        }

        protected override bool CheckAncestorJump(PeerInfo? bestPeer, Hash256? startHeaderHash, ref long currentNumber)
        {
            // No ancestor jump check post merge.
            return true;
        }

        protected override bool CheckAncestorJump(PeerInfo? bestPeer, BlockDownloadContext context, ref long currentNumber)
        {
            // No ancestor jump check post merge.
            return true;
        }

        protected override BlockTreeSuggestOptions GetSuggestOption(bool shouldProcess, Block currentBlock)
        {
            BlockTreeSuggestOptions suggestOptions =
                shouldProcess ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None;

            bool isKnownBeaconBlock = _blockTree.IsKnownBeaconBlock(currentBlock.Number, currentBlock.GetOrCalculateHash());
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
            return suggestOptions;
        }

        protected override void OnBlockAdded(Block currentBlock)
        {
            if ((_beaconPivot.ProcessDestination?.Number ?? long.MaxValue) < currentBlock.Number)
            {
                // Move the process destination in front, otherwise `ChainLevelHelper` would continue returning
                // already processed header starting from `ProcessDestination`.
                _beaconPivot.ProcessDestination = currentBlock.Header;
            }
        }
    }
}
