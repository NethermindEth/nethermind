// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Threading;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class BlockImprovementContextFactory(IBlockProducer blockProducer, TimeSpan timeout)
    : IBlockImprovementContextFactory
{
    public IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        UInt256 currentBlockFees,
        SharedCancellationTokenSource cts) =>
        new BlockImprovementContext(currentBestBlock, blockProducer, timeout, parentHeader, payloadAttributes, startDateTime, currentBlockFees, cts);
}
