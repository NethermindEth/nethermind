// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class BeaconHeadersSyncDispatcher : HeadersSyncDispatcher
{
    public BeaconHeadersSyncDispatcher(
        int maxNumberOfProcessingThread,
        ISyncFeed<HeadersSyncBatch> syncFeed,
        ISyncPeerPool syncPeerPool,
        IPeerAllocationStrategyFactory<FastBlocksBatch> peerAllocationStrategy,
        ILogManager logManager)
        : base(maxNumberOfProcessingThread, syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
    {
    }
}
