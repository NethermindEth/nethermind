// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization.Test.FastSync.SnapProtocolTests
{
    public class StateSyncDispatcherTester : StateSyncDispatcher
    {
        public StateSyncDispatcherTester(
            ISyncFeed<StateSyncBatch> syncFeed,
            ISyncPeerPool syncPeerPool,
            IPeerAllocationStrategyFactory<StateSyncBatch> peerAllocationStrategy,
            ILogManager logManager) : base(0, syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
        }

        public async Task ExecuteDispatch(StateSyncBatch batch, int times)
        {
            SyncPeerAllocation allocation = await Allocate(batch);

            for (int i = 0; i < times; i++)
            {
                await base.Dispatch(allocation.Current, batch, CancellationToken.None);
            }
        }
    }
}
