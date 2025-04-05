// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class NoBlockImprovementContextFactory : IBlockImprovementContextFactory
{
    public static NoBlockImprovementContextFactory Instance { get; } = new();

    public IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        UInt256 currentBlockFees,
        CancellationTokenSource cts)
    {
        return new NoBlockImprovementContext(currentBestBlock, UInt256.Zero, startDateTime);
    }
}
