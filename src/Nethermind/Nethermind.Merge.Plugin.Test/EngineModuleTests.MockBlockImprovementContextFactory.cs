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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    private class MockBlockImprovementContextFactory : IBlockImprovementContextFactory
    {
        public IBlockImprovementContext StartBlockImprovementContext(Block currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes, DateTimeOffset startDateTime) =>
            new MockBlockImprovementContext(currentBestBlock, startDateTime);
    }

    private class MockBlockImprovementContext : IBlockImprovementContext
    {
        public MockBlockImprovementContext(Block currentBestBlock, DateTimeOffset startDateTime)
        {
            CurrentBestBlock = currentBestBlock;
            StartDateTime = startDateTime;
            ImprovementTask = Task.FromResult((Block?)currentBestBlock);
        }

        public void Dispose() => Disposed = true;
        public Task<Block?> ImprovementTask { get; }
        public Block? CurrentBestBlock { get; }
        public bool Disposed { get; private set; }
        public DateTimeOffset StartDateTime { get; }
    }
}
