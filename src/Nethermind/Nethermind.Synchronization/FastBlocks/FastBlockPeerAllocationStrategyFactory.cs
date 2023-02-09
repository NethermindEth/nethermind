// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.FastBlocks
{
    public class FastBlocksPeerAllocationStrategyFactory : IPeerAllocationStrategyFactory<FastBlocksBatch>
    {
        public IPeerAllocationStrategy Create(FastBlocksBatch request)
        {
            TransferSpeedType speedType = TransferSpeedType.Latency;
            if (request is HeadersSyncBatch)
            {
                speedType = TransferSpeedType.Headers;
            }
            else if (request is BodiesSyncBatch)
            {
                speedType = TransferSpeedType.Bodies;
            }
            else if (request is ReceiptsSyncBatch)
            {
                speedType = TransferSpeedType.Receipts;
            }

            return new FastBlocksAllocationStrategy(speedType, request.MinNumber, request.Prioritized);
        }
    }
}
