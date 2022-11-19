//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

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
