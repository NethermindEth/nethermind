// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization.Test.FastSync.SnapProtocolTests
{
    public class StateSyncDispatcherTester(
        ISyncDownloader<StateSyncBatch> downloader,
        ISyncPeerPool syncPeerPool) : IAsyncDisposable
    {
        private readonly ISyncConfig _syncConfig = new SyncConfig() { SyncDispatcherEmptyRequestDelayMs = 1, SyncDispatcherAllocateTimeoutMs = 1 };

        public async Task ExecuteDispatch(StateSyncBatch batch, int times)
        {
            StateSyncAllocationStrategyFactory strategyFactory = new();
            SyncPeerAllocation allocation = await syncPeerPool.Allocate(
                strategyFactory.Create(batch), AllocationContexts.State, _syncConfig.SyncDispatcherAllocateTimeoutMs);

            for (int i = 0; i < times; i++)
            {
                await downloader.Dispatch(allocation.Current!, batch, CancellationToken.None);
            }

            syncPeerPool.Free(allocation);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
