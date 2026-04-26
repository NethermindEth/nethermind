// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Taiko.Rpc;

internal class TaikoForkchoiceUpdatedHandler(
    IBlockTree blockTree,
    IManualBlockFinalizationManager manualBlockFinalizationManager,
    IPoSSwitcher poSSwitcher,
    IPayloadPreparationService payloadPreparationService,
    IBlockProcessingQueue processingQueue,
    IBlockCacheService blockCacheService,
    IInvalidChainTracker invalidChainTracker,
    IMergeSyncController mergeSyncController,
    IBeaconPivot beaconPivot,
    IPeerRefresher peerRefresher,
    ISpecProvider specProvider,
    ISyncPeerPool syncPeerPool,
    IMergeConfig mergeConfig,
    ILogManager logManager
) : ForkchoiceUpdatedHandler(
    blockTree,
    manualBlockFinalizationManager,
    poSSwitcher,
    payloadPreparationService,
    processingQueue,
    blockCacheService,
    invalidChainTracker,
    mergeSyncController,
    beaconPivot,
    peerRefresher,
    specProvider,
    syncPeerPool,
    mergeConfig,
    logManager)
{
    protected override bool IsOnMainChainBehindHead(BlockHeader newHeadHeader, ForkchoiceStateV1 forkchoiceState,
        [NotNullWhen(false)] out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult)
    {
        errorResult = null;
        return true;
    }

    // Taiko finality follows L1 and may regress on L1 reorgs, so Ethereum's spec-ordering bounds
    // on finalized (Casper FFG monotonicity) and safe (safe >= finalized) don't apply. Keep the
    // ancestry check via the base call; pass lowerBound=0 to disable the numeric bound.
    protected override ResultWrapper<ForkchoiceUpdatedV1Result>? RejectIfInconsistent(
        BlockHeader? header, long lowerBound, string label, BlockHeader newHeadHeader, string requestStr)
        => base.RejectIfInconsistent(header, 0, label, newHeadHeader, requestStr);

    protected override BlockHeader? ValidateBlockHash(ref Hash256 blockHash, out string? errorMessage, bool skipZeroHash = true)
    {
        errorMessage = null;
        if (skipZeroHash && blockHash == Keccak.Zero)
        {
            return null;
        }

        BlockHeader? blockHeader = _blockTree.FindHeader(blockHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (blockHeader is null)
        {
            blockHash = Keccak.Zero;
            return null;
        }

        return blockHeader;
    }

    // Taiko allows equal timestamps because multiple L2 blocks can be derived
    // from a single L1 block, all sharing the same L1 anchor timestamp.
    protected override bool IsPayloadTimestampValid(BlockHeader newHeadHeader, PayloadAttributes payloadAttributes)
        => payloadAttributes.Timestamp >= newHeadHeader.Timestamp;

    protected override bool TryGetBranch(BlockHeader newHeadHeader, out IReadOnlyList<Block> blocks)
    {
        // Allow resetting to any block already on the main chain (including genesis)
        if (_blockTree.IsMainChain(newHeadHeader))
        {
            Block? newHeadBlock = _blockTree.FindBlock(newHeadHeader.Hash!, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            if (newHeadBlock is null)
            {
                blocks = [];
                return false;
            }
            blocks = [newHeadBlock];
            return true;
        }

        return base.TryGetBranch(newHeadHeader, out blocks);
    }
}
