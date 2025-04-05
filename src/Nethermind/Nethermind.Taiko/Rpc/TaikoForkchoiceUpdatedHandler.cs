// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Taiko.Rpc;

internal class TaikoForkchoiceUpdatedHandler(IBlockTree blockTree,
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
    ILogManager logManager,
    bool simulateBlockProduction = false) : ForkchoiceUpdatedHandler(blockTree,
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
          logManager,
          simulateBlockProduction)
{
    protected override bool IsOnMainChainBehindHead(Block newHeadBlock, ForkchoiceStateV1 forkchoiceState,
       [NotNullWhen(false)] out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult)
    {
        errorResult = null;
        return true;
    }

    protected override bool IsPayloadAttributesTimestampValid(Block newHeadBlock, ForkchoiceStateV1 forkchoiceState, PayloadAttributes payloadAttributes,
        [NotNullWhen(false)] out ResultWrapper<ForkchoiceUpdatedV1Result>? errorResult)
    {
        if (newHeadBlock.Timestamp > payloadAttributes.Timestamp)
        {
            string error = $"Payload timestamp {payloadAttributes.Timestamp} must be greater or equal to head block timestamp {newHeadBlock.Timestamp}.";
            errorResult = ForkchoiceUpdatedV1Result.Error(error, MergeErrorCodes.InvalidPayloadAttributes);
            return false;
        }

        errorResult = null;
        return true;
    }

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
}
