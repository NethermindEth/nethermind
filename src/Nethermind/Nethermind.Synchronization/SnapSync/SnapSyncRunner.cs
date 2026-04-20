// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync;

public class SnapSyncRunner(
    ISnapProvider snapProvider,
    ISyncPeerPool syncPeerPool,
    ISyncConfig syncConfig,
    ILogManager logManager) : ISnapSyncRunner
{
    public Task Run(CancellationToken token)
    {
        SimpleDispatcher dispatcher = new(syncPeerPool, syncConfig, logManager);
        return dispatcher.RunFeed(
            new SnapSyncFeed(snapProvider, logManager),
            new SnapSyncDownloader(logManager),
            new SnapSyncAllocationStrategyFactory(),
            AllocationContexts.Snap,
            token);
    }
}
