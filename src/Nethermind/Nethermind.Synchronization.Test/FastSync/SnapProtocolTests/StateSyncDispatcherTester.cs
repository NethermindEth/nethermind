// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Test.FastSync.SnapProtocolTests
{
    public class StateSyncDispatcherTester(
        ISyncFeed<StateSyncBatch> syncFeed,
        ISyncDownloader<StateSyncBatch> downloader,
        ISyncPeerPool syncPeerPool,
        IPeerAllocationStrategyFactory<StateSyncBatch> peerAllocationStrategy,
        ILogManager logManager) : SyncDispatcher<StateSyncBatch>(new SyncConfig() { SyncDispatcherEmptyRequestDelayMs = 1, SyncDispatcherAllocateTimeoutMs = 1 }, syncFeed, downloader, syncPeerPool, peerAllocationStrategy, logManager)
    {
        private readonly ISyncDownloader<StateSyncBatch> _downloader = downloader;

        public async Task ExecuteDispatch(StateSyncBatch batch, int times)
        {
            SyncPeerAllocation allocation = await Allocate(batch, default);

            for (int i = 0; i < times; i++)
            {
                await _downloader.Dispatch(allocation.Current!, batch, CancellationToken.None);
            }
        }
    }
}
