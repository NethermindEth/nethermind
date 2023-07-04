// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
    public class StateSyncDispatcherTester : SyncDispatcher<StateSyncBatch>
    {
        private readonly ISyncDownloader<StateSyncBatch> _downloader;

        public StateSyncDispatcherTester(
            ISyncFeed<StateSyncBatch> syncFeed,
            ISyncDownloader<StateSyncBatch> downloader,
            ISyncPeerPool syncPeerPool,
            IPeerAllocationStrategyFactory<StateSyncBatch> peerAllocationStrategy,
            ILogManager logManager) : base(0, syncFeed, downloader, syncPeerPool, peerAllocationStrategy, logManager)
        {
            _downloader = downloader;
        }

        public async Task ExecuteDispatch(StateSyncBatch batch, int times)
        {
            SyncPeerAllocation allocation = await Allocate(batch);

            for (int i = 0; i < times; i++)
            {
                await _downloader.Dispatch(allocation.Current, batch, CancellationToken.None);
            }
        }
    }
}
