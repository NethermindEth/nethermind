// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.History;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Merge.Plugin.Synchronization
{
    public class MergeBlockDownloader(
        IBeaconPivot beaconPivot,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        ISyncReport syncReport,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        IBetterPeerStrategy betterPeerStrategy,
        IFullStateFinder fullStateFinder,
        IForwardHeaderProvider forwardHeaderProvider,
        ISyncPeerPool syncPeerPool,
        IReceiptsRecovery receiptsRecovery,
        IBlockProcessingQueue blockProcessingQueue,
        ISyncConfig syncConfig,
        IBlocksConfig blocksConfig,
        BlockAccessListServingWindow balServingWindow,
        IBlockAccessListStore balStore,
        ILogManager logManager,
        FinalizedBlockAccessListPolicy? finalizedBalPolicy = null)
        : BlockDownloader(
            blockTree,
            blockValidator,
            syncReport,
            receiptStorage,
            specProvider,
            betterPeerStrategy,
            fullStateFinder,
            forwardHeaderProvider,
            syncPeerPool,
            receiptsRecovery,
            blockProcessingQueue,
            syncConfig,
            balServingWindow,
            logManager)
    {
        private readonly IBlockTree _blockTree = blockTree;
        private readonly ILogger _logger = logManager.GetClassLogger<MergeBlockDownloader>();
        private readonly bool _executionUsesAccessLists = BlockAccessListRecoveryStep.ExecutionUsesAccessLists(blocksConfig);
        private readonly BlockAccessListServingWindow _balServingWindow = balServingWindow;

        /// <inheritdoc/>
        /// <remarks>
        /// <c>engine_newPayload</c> stores the bodies of beacon blocks at or above the beacon pivot, while beacon headers sync
        /// fills the levels below it with headers only, so the block store is read only for levels marked as having a beacon body.
        /// </remarks>
        protected override Block? FindLocalBlock(BlockHeader header) =>
            beaconPivot.BeaconPivotExists()
            && header.Number >= beaconPivot.PivotNumber
            && header.Hash is { } hash
            && _blockTree.GetInfo(header.Number, hash).Info is { IsBeaconBody: true }
                ? _blockTree.FindBlock(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing, header.Number)
                : null;

        /// <inheritdoc/>
        /// <remarks>
        /// A block that executes also takes its list when parallel execution or read warming uses it, as a block from
        /// <c>engine_newPayload</c> does. Such a block executes without the list when peers do not serve it. Below the serving
        /// window of the best known header, peers may have pruned the list, so the block does not wait for retries that fail.
        /// A list already in the store, e.g. from <c>engine_newPayload</c> for a stored beacon body, is not downloaded again:
        /// processing takes it from the store.
        /// </remarks>
        protected override bool ShouldDownloadAccessList(BlockHeader header, bool shouldProcess) =>
            base.ShouldDownloadAccessList(header, shouldProcess)
            || (shouldProcess && _executionUsesAccessLists && _balServingWindow.Contains(header.Number, ulong.MaxValue)
                && !balStore.Exists(header.Number, header.Hash!))
            || finalizedBalPolicy?.CanReconstruct(header) == true;

        protected override bool ShouldDownloadReceipts(BlockHeader header, bool shouldProcess, bool requested) =>
            base.ShouldDownloadReceipts(header, shouldProcess, requested)
            || (shouldProcess && header.HasTransactions && finalizedBalPolicy?.CanReconstruct(header) == true);

        protected override BlockTreeSuggestOptions GetSuggestOption(bool shouldProcess, Block currentBlock)
        {
            BlockTreeSuggestOptions suggestOptions = base.GetSuggestOption(shouldProcess, currentBlock);

            bool isKnownBeaconBlock = _blockTree.IsKnownBeaconBlock(currentBlock.Number, currentBlock.GetOrCalculateHash());
            if (_logger.IsTrace) _logger.Trace($"Current block {currentBlock}, BeaconPivot: {beaconPivot.PivotNumber}, IsKnownBeaconBlock: {isKnownBeaconBlock}");

            if (isKnownBeaconBlock)
            {
                suggestOptions |= BlockTreeSuggestOptions.FillBeaconBlock;
            }

            if (_logger.IsTrace)
                _logger.Trace(
                    $"MergeBlockDownloader - SuggestBlock {currentBlock}, IsKnownBeaconBlock {isKnownBeaconBlock} ShouldProcess: {shouldProcess}");
            return suggestOptions;
        }
    }
}
