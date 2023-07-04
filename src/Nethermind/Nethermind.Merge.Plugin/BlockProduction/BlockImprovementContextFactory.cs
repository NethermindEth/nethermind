// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Org.BouncyCastle.Asn1.Cms;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class BlockImprovementContextFactory : IBlockImprovementContextFactory
{
    private readonly IManualBlockProductionTrigger _blockProductionTrigger;
    private readonly TimeSpan _timeout;

    public BlockImprovementContextFactory(IManualBlockProductionTrigger blockProductionTrigger, TimeSpan timeout)
    {
        _blockProductionTrigger = blockProductionTrigger;
        _timeout = timeout;
    }

    public IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime) =>
        new BlockImprovementContext(currentBestBlock, _blockProductionTrigger, _timeout, parentHeader, payloadAttributes, startDateTime);
}
