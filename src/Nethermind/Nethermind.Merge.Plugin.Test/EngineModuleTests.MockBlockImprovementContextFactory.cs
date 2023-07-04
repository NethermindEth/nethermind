// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Int256;
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
        public UInt256 BlockFees { get; }
        public bool Disposed { get; private set; }
        public DateTimeOffset StartDateTime { get; }
    }
}
