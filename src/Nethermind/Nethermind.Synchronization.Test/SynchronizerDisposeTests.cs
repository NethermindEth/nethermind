// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class SynchronizerDisposeTests
{
    [Test]
    public async Task DisposeAsync_is_idempotent()
    {
        ISyncFeed<BlocksRequest> fullSyncFeed = Substitute.For<ISyncFeed<BlocksRequest>>();
        Synchronizer synchronizer = new(
            Substitute.For<ISyncModeSelector>(),
            Substitute.For<ISyncReport>(),
            Substitute.For<ISyncConfig>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<ISyncPivotResolver>(),
            LimboLogs.Instance,
            Substitute.For<INodeStatsManager>(),
            new SyncFeedComponent<BlocksRequest>(fullSyncFeed, null!, null!, null!, null!),
            new SyncFeedComponent<BlocksRequest>(Substitute.For<ISyncFeed<BlocksRequest>>(), null!, null!, null!, null!),
            Substitute.For<IStateSyncRunner>(),
            new SyncFeedComponent<HeadersSyncBatch>(Substitute.For<ISyncFeed<HeadersSyncBatch>>(), null!, null!, null!, null!),
            new SyncFeedComponent<BodiesSyncBatch>(Substitute.For<ISyncFeed<BodiesSyncBatch>>(), null!, null!, null!, null!),
            new SyncFeedComponent<ReceiptsSyncBatch>(Substitute.For<ISyncFeed<ReceiptsSyncBatch>>(), null!, null!, null!, null!),
            new SyncFeedComponent<BlockAccessListsSyncBatch>(Substitute.For<ISyncFeed<BlockAccessListsSyncBatch>>(), null!, null!, null!, null!),
            null!,
            null!,
            Substitute.For<IProcessExitSource>());

        await synchronizer.DisposeAsync();
        await synchronizer.DisposeAsync();

        // Container teardown disposes twice (dispose tracking); the second run must not wait on
        // the feed tasks again - with a stuck feed it would pay the full termination timeout twice.
        _ = fullSyncFeed.Received(1).FeedTask;
    }
}
