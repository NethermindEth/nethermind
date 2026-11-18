// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.FastBlocks
{
    public class FastBlocksPeerAllocationStrategyFactory : IPeerAllocationStrategyFactory<FastBlocksBatch>
    {
        private readonly IPeerAllocationStrategy _slowestHeader = new BySpeedStrategy(TransferSpeedType.Headers, false);

        public IPeerAllocationStrategy Create(FastBlocksBatch request)
        {
            TransferSpeedType speedType = request switch
            {
                HeadersSyncBatch => TransferSpeedType.Headers,
                BodiesSyncBatch => TransferSpeedType.Bodies,
                ReceiptsSyncBatch => TransferSpeedType.Receipts,
                BlockAccessListsSyncBatch => TransferSpeedType.BlockAccessLists,
                _ => TransferSpeedType.Latency
            };

            return speedType == TransferSpeedType.Headers
                ? new FastBlocksAllocationStrategy(speedType, request.MinNumber, request.Prioritized, _slowestHeader)
                : new FastBlocksAllocationStrategy(speedType, request.MinNumber, request.Prioritized);
        }
    }
}
