// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Optimism;

public class OptimismPayloadPreparationService : PayloadPreparationService
{
    private readonly ILogger _logger;

    public OptimismPayloadPreparationService(PostMergeBlockProducer blockProducer,
        IBlockImprovementContextFactory blockImprovementContextFactory, ITimerFactory timerFactory,
        ILogManager logManager, TimeSpan timePerSlot, int slotsPerOldPayloadCleanup = SlotsPerOldPayloadCleanup,
        TimeSpan? improvementDelay = null, TimeSpan? minTimeForProduction = null) : base(blockProducer,
        blockImprovementContextFactory, timerFactory, logManager, timePerSlot, slotsPerOldPayloadCleanup,
        improvementDelay, minTimeForProduction)
    {
        _logger = logManager.GetClassLogger();
    }

    // public override string StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
    // {
    //     if (payloadAttributes is OptimismPayloadAttributes { NoTxPool: false })
    //         return base.StartPreparingPayload(parentHeader, payloadAttributes);
    //
    //     string payloadId = payloadAttributes.GetPayloadId(parentHeader);
    //     if (!_payloadStorage.ContainsKey(payloadId))
    //     {
    //         Block emptyBlock = ProduceEmptyBlock(payloadId, parentHeader, payloadAttributes);
    //         NoBlockImprovementContext noBlockImprovementContext = new(emptyBlock, UInt256.Zero, DateTimeOffset.Now);
    //         if (!_payloadStorage.TryAdd(payloadId, noBlockImprovementContext))
    //             _logger.Warn($"TryAdd empty (deposit only) payload failed. PayloadId: {payloadId}");
    //     }
    //     else if (_logger.IsInfo)
    //         _logger.Info($"Payload with the same parameters has already started. PayloadId: {payloadId}");
    //
    //     return payloadId;
    // }

    protected override void ImproveBlock(string payloadId, BlockHeader parentHeader,
        PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime)
    {
        if (payloadAttributes is OptimismPayloadAttributes { NoTxPool: false })
            base.ImproveBlock(payloadId, parentHeader, payloadAttributes, currentBestBlock, startDateTime);
        else
        {
            if (_logger.IsInfo)
                _logger.Info($"Skip block improvement because of NoTxPool payload attribute.");

            // ignore TryAdd failure (it can only happen if payloadId is already in the dictionary)
            _payloadStorage.TryAdd(payloadId,
                new NoBlockImprovementContext(currentBestBlock, UInt256.Zero, startDateTime));
        }
    }
}
