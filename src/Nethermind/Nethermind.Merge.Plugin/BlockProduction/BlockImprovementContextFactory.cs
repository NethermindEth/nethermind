// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

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
        DateTimeOffset startDateTime) =>
        new BlockImprovementContext(currentBestBlock, _blockProducer, _timeout, parentHeader, payloadAttributes, startDateTime);
}
