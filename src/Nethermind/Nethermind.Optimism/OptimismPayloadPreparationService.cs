// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Optimism.ExtraParams;
using Nethermind.Optimism.Rpc;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismPayloadPreparationService : PayloadPreparationService
{
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    public OptimismPayloadPreparationService(
        ISpecProvider specProvider,
        IBlockProducer blockProducer,
        ITxPool txPool,
        IBlockImprovementContextFactory blockImprovementContextFactory,
        ITimerFactory timerFactory,
        ILogManager logManager,
        IBlocksConfig blocksConfig)
        : base(
            blockProducer,
            txPool,
            blockImprovementContextFactory,
            timerFactory,
            logManager,
            blocksConfig)
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
                if (!HoloceneExtraParams.TryParse(optimismPayload, out HoloceneExtraParams parameters, out var error))
                {
                    throw new InvalidOperationException($"{nameof(BlockHeader)} was not properly validated: {error}");
                }

                if (parameters.IsZero())
                {
                    parameters = parameters with { Denominator = (UInt32)spec.BaseFeeMaxChangeDenominator, Elasticity = (UInt32)spec.ElasticityMultiplier };
                }

                currentBestBlock.Header.ExtraData = new byte[HoloceneExtraParams.BinaryLength];
                parameters.WriteTo(currentBestBlock.Header.ExtraData);
            }

            if (false /* spec.IsOpJovianEnabled */)
#pragma warning disable CS0162 // Unreachable code detected
            {
                // NOTE: This operation should never fail since headers should be valid at this point.
                if (!JovianExtraParams.TryParse(optimismPayload, out JovianExtraParams parameters, out var error))
                {
                    throw new InvalidOperationException($"{nameof(BlockHeader)} was not properly validated: {error}");
                }

                if (parameters.IsZero())
                {
                    parameters = parameters with { Denominator = (UInt32)spec.BaseFeeMaxChangeDenominator, Elasticity = (UInt32)spec.ElasticityMultiplier };
                }

                currentBestBlock.Header.ExtraData = new byte[JovianExtraParams.BinaryLength];
                parameters.WriteTo(currentBestBlock.Header.ExtraData);
            }
#pragma warning restore CS0162 // Unreachable code detected

            // NOTE: Since we updated the `Header` we need to recalculate the hash.
            currentBestBlock.Header.Hash = currentBestBlock.Header.CalculateHash();
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
