// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism;

public class OptimismPayloadPreparationService : PayloadPreparationService
{
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    public OptimismPayloadPreparationService(
        ISpecProvider specProvider,
        PostMergeBlockProducer blockProducer,
        IBlockImprovementContextFactory blockImprovementContextFactory,
        ITimerFactory timerFactory,
        ILogManager logManager,
        TimeSpan timePerSlot,
        int slotsPerOldPayloadCleanup = SlotsPerOldPayloadCleanup,
        TimeSpan? improvementDelay = null)
        : base(
            blockProducer,
            blockImprovementContextFactory,
            timerFactory,
            logManager,
            timePerSlot,
            slotsPerOldPayloadCleanup,
            improvementDelay)
    {
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
    }

    protected override void ImproveBlock(string payloadId, BlockHeader parentHeader,
        PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime, UInt256 currentBlockFees, CancellationTokenSource cts)
    {
        if (payloadAttributes is OptimismPayloadAttributes optimismPayload)
        {
            var spec = _specProvider.GetSpec(currentBestBlock.Header);
            if (spec.IsOpHoloceneEnabled)
            {
                // NOTE: This operation should never fail since headers should be valid at this point.
                if (!optimismPayload.TryDecodeEIP1559Parameters(out EIP1559Parameters eip1559Parameters, out var error))
                {
                    throw new InvalidOperationException($"{nameof(BlockHeader)} was not properly validated: {error}");
                }

                if (eip1559Parameters.IsZero())
                {
                    eip1559Parameters = new EIP1559Parameters(eip1559Parameters.Version, (UInt32)spec.BaseFeeMaxChangeDenominator, (UInt32)spec.ElasticityMultiplier);
                }

                currentBestBlock.Header.ExtraData = new byte[EIP1559Parameters.ByteLength];
                eip1559Parameters.WriteTo(currentBestBlock.Header.ExtraData);

                // NOTE: Since we updated the `Header` we need to recalculate the hash.
                currentBestBlock.Header.Hash = currentBestBlock.Header.CalculateHash();
            }
        }

        if (payloadAttributes is OptimismPayloadAttributes { NoTxPool: true })
        {
            if (_logger.IsDebug)
                _logger.Debug("Skip block improvement because of NoTxPool payload attribute.");

            // ignore TryAdd failure (it can only happen if payloadId is already in the dictionary)
            _payloadStorage.TryAdd(payloadId,
                new NoBlockImprovementContext(currentBestBlock, UInt256.Zero, startDateTime));
        }
        else
        {
            base.ImproveBlock(payloadId, parentHeader, payloadAttributes, currentBestBlock, startDateTime, currentBlockFees, cts);
        }
    }
}
