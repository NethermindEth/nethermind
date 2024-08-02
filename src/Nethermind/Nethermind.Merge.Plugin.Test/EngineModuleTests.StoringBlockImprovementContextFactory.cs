// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
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
