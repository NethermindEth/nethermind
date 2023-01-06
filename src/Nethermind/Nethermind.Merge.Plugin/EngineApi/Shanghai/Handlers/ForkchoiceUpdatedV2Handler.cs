// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.EngineApi.Paris.Data;
using Nethermind.Merge.Plugin.EngineApi.Paris.Handlers;
using Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;

namespace Nethermind.Merge.Plugin.EngineApi.Shanghai.Handlers;

/// <summary>
/// Propagates the change in the fork choice to the execution client. May initiate creating new payload.
/// <see href="https://github.com/ethereum/execution-apis/blob/main/src/engine/shanghai.md#engine_forkchoiceupdatedv2">engine_forkchoiceupdatedv2</see>.
/// </summary>
public abstract class ForkchoiceUpdatedV2AbstractHandler<TForkChoiceState, TPayloadAttributes, TResult>
    : ForkchoiceUpdatedV1AbstractHandler<TForkChoiceState, TPayloadAttributes, TResult>
    where TForkChoiceState : ForkchoiceStateV1
    where TPayloadAttributes : PayloadAttributesV2
    where TResult : ForkchoiceUpdatedV1Result, IForkchoiceUpdatedResult<TResult>
{
    protected ForkchoiceUpdatedV2AbstractHandler(
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
        ILogManager logManager)
        : base(
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
            logManager)
    {
    }

    protected override bool ValidatePayload(
        Block newHeadBlock,
        TPayloadAttributes payloadAttributes,
        [NotNullWhen(false)] out ResultWrapper<TResult>? errorResult)
    {
        if (base.ValidatePayload(newHeadBlock, payloadAttributes, out errorResult))
        {
            IReleaseSpec spec = _specProvider.GetSpec(newHeadBlock.Number + 1, payloadAttributes.Timestamp);

            if (spec.WithdrawalsEnabled && payloadAttributes.Withdrawals is null)
            {
                string error = "Withdrawals cannot be null when EIP-4895 activated.";

                if (_logger.IsInfo) _logger.Warn($"Invalid payload attributes: {error}");

                {
                    errorResult = TResult.Error(error, MergeErrorCodes.InvalidPayloadAttributes);
                    return false;
                }
            }

            if (!spec.WithdrawalsEnabled && payloadAttributes.Withdrawals is not null)
            {
                string error = "Withdrawals must be null when EIP-4895 not activated.";

                if (_logger.IsInfo) _logger.Warn($"Invalid payload attributes: {error}");

                {
                    errorResult = TResult.Error(error, MergeErrorCodes.InvalidPayloadAttributes);
                    return false;
                }
            }

            errorResult = null;
            return true;
        }

        return false;
    }
}


public sealed class ForkchoiceUpdatedV2Handler : ForkchoiceUpdatedV2AbstractHandler<ForkchoiceStateV1, PayloadAttributesV2, ForkchoiceUpdatedV1Result>
{
    public ForkchoiceUpdatedV2Handler(
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
        ILogManager logManager)
        : base(
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
            logManager)
    {
    }
}
