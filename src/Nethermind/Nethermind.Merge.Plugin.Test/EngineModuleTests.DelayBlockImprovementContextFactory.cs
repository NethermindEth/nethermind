﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Int256;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    private class DelayBlockImprovementContextFactory : IBlockImprovementContextFactory
    {
        private readonly IManualBlockProductionTrigger _productionTrigger;
        private readonly TimeSpan _timeout;
        private readonly TimeSpan _delay;

        public DelayBlockImprovementContextFactory(IManualBlockProductionTrigger productionTrigger, TimeSpan timeout, TimeSpan delay)
        {
            _productionTrigger = productionTrigger;
            _timeout = timeout;
            _delay = delay;
        }

        public IBlockImprovementContext StartBlockImprovementContext(Block currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes, DateTimeOffset startDateTime) =>
            new DelayBlockImprovementContext(currentBestBlock, _productionTrigger, _timeout, parentHeader, payloadAttributes, _delay, startDateTime);
    }

    private class DelayBlockImprovementContext : IBlockImprovementContext
    {
        private CancellationTokenSource? _cancellationTokenSource;

        public DelayBlockImprovementContext(Block currentBestBlock,
            IManualBlockProductionTrigger blockProductionTrigger,
            TimeSpan timeout,
            BlockHeader parentHeader,
            PayloadAttributes payloadAttributes,
            TimeSpan delay,
            DateTimeOffset startDateTime)
        {
            _cancellationTokenSource = new CancellationTokenSource(timeout);
            Block = currentBestBlock;
            StartDateTime = startDateTime;
            ImprovementTask = BuildBlock(blockProductionTrigger, parentHeader, payloadAttributes, delay, _cancellationTokenSource.Token);
        }

        private async Task<Block?> BuildBlock(
            IManualBlockProductionTrigger blockProductionTrigger,
            BlockHeader parentHeader,
            PayloadAttributes payloadAttributes,
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            Block? block = await blockProductionTrigger.BuildBlock(parentHeader, cancellationToken, NullBlockTracer.Instance, payloadAttributes);
            await Task.Delay(delay, cancellationToken);
            if (block is not null)
            {
                Block = block;
            }

            return Block;
        }

        public Task<Block?> ImprovementTask { get; }
        public Block? Block { get; private set; }
        public UInt256 BlockFees { get; }
        public bool Disposed { get; private set; }
        public DateTimeOffset StartDateTime { get; }

        public void Dispose()
        {
            Disposed = true;
            CancellationTokenExtensions.CancelDisposeAndClear(ref _cancellationTokenSource);
        }
    }
}
