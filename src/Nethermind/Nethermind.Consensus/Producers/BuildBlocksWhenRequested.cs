// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Producers
{
    public class BuildBlocksWhenRequested : IManualBlockProductionTrigger
    {
        public BuildBlocksWhenRequested()
        {
            Console.WriteLine($"[BuildBlocksWhenRequested] NEW INSTANCE CREATED: {GetHashCode()}");
        }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        public Task<Block?> BuildBlock(
            BlockHeader? parentHeader = null,
            CancellationToken? cancellationToken = null,
            IBlockTracer? blockTracer = null,
            PayloadAttributes? payloadAttributes = null)
        {
            int subscriberCount = TriggerBlockProduction?.GetInvocationList()?.Length ?? 0;
            Console.WriteLine($"[BuildBlocksWhenRequested] BuildBlock called. Subscribers: {subscriberCount}, Parent: {parentHeader?.Number}");

            BlockProductionEventArgs args = new(parentHeader, cancellationToken, blockTracer, payloadAttributes);

            if (subscriberCount > 0)
            {
                Console.WriteLine($"[BuildBlocksWhenRequested] Invoking event with {subscriberCount} subscribers...");
                TriggerBlockProduction?.Invoke(this, args);
                Console.WriteLine($"[BuildBlocksWhenRequested] Event invoked. Task is {(args.BlockProductionTask == null ? "NULL" : "SET")}");
            }
            else
            {
                Console.WriteLine($"[BuildBlocksWhenRequested] NO SUBSCRIBERS - cannot produce block!");
            }

            return args.BlockProductionTask;
        }
    }
}
