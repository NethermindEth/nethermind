// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class BlockImprovementContextFactory : IBlockImprovementContextFactory
{
    private readonly IBlockProducer _blockProducer;
    private readonly TimeSpan _timeout;

    public BlockImprovementContextFactory(IBlockProducer blockProducer, TimeSpan timeout)
    {
        _blockProducer = blockProducer;
        _timeout = timeout;
    }

    public IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        UInt256 currentBlockFees,
        CancellationTokenSource cts) =>
        new BlockImprovementContext(currentBestBlock, _blockProducer, _timeout, parentHeader, payloadAttributes, startDateTime, currentBlockFees, cts);
}
