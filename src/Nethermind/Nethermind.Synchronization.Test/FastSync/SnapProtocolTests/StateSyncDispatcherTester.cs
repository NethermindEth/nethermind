// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

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
                await _downloader.Dispatch(allocation.Current!, batch, CancellationToken.None);
            }
        }
    }
}
