// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Core.Timers;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Optimism.Rpc;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismPayloadPreparationService(
    ISpecProvider specProvider,
    IBlockProducer blockProducer,
    ITxPool txPool,
    IBlockImprovementContextFactory blockImprovementContextFactory,
    ITimerFactory timerFactory,
    ILogManager logManager,
    IBlocksConfig blocksConfig) : PayloadPreparationService(
        blockProducer,
        txPool,
        blockImprovementContextFactory,
        timerFactory,
        logManager,
        blocksConfig,
        specProvider)
{
    private readonly ILogger _logger = logManager.GetClassLogger<OptimismPayloadPreparationService>();

    protected override void ImproveBlock(string payloadId, BlockHeader parentHeader,
        PayloadAttributes payloadAttributes, Block? currentBestBlock, DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts)
    {
        if (payloadAttributes is OptimismPayloadAttributes { NoTxPool: true })
        {
            if (_logger.IsDebug)
                _logger.Debug("Skip block improvement because of NoTxPool payload attribute.");

            cts.CancelAndDispose();

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
