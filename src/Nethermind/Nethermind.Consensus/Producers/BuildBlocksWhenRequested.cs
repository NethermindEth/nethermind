// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Producers
{
    public class BuildBlocksWhenRequested : IManualBlockProductionTrigger
    {
        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        public Task<Block?> BuildBlock(
            BlockHeader? parentHeader = null,
            CancellationToken? cancellationToken = null,
            IBlockTracer? blockTracer = null,
            PayloadAttributes payloadAttributes = null)
        {
            BlockProductionEventArgs args = new(parentHeader, cancellationToken, blockTracer, payloadAttributes);
            TriggerBlockProduction?.Invoke(this, args);
            return args.BlockProductionTask;
        }
    }
}
