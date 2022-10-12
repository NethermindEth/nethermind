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
using System.Collections.Generic;
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
    private class StoringBlockImprovementContextFactory : IBlockImprovementContextFactory
    {
        private readonly IBlockImprovementContextFactory _blockImprovementContextFactory;
        public IList<IBlockImprovementContext> CreatedContexts { get; } = new List<IBlockImprovementContext>();

        public event EventHandler<ImprovementStartedEventArgs>? ImprovementStarted;

        public StoringBlockImprovementContextFactory(IBlockImprovementContextFactory blockImprovementContextFactory)
        {
            _blockImprovementContextFactory = blockImprovementContextFactory;
        }

        public IBlockImprovementContext StartBlockImprovementContext(Block currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes, DateTimeOffset startDateTime)
        {
            IBlockImprovementContext blockImprovementContext = _blockImprovementContextFactory.StartBlockImprovementContext(currentBestBlock, parentHeader, payloadAttributes, startDateTime);
            CreatedContexts.Add(blockImprovementContext);
            Task.Run(() => ImprovementStarted?.Invoke(this, new ImprovementStartedEventArgs(blockImprovementContext)));
            return blockImprovementContext;
        }
    }

    private class ImprovementStartedEventArgs : EventArgs
    {
        public IBlockImprovementContext BlockImprovementContext { get; }

        public ImprovementStartedEventArgs(IBlockImprovementContext blockImprovementContext)
        {
            BlockImprovementContext = blockImprovementContext;
        }
    }
}
