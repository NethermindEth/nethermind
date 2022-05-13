using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Synchronization.ParallelSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture(false)]
    [TestFixture(true)]
    public class MultiSyncModeSelectorSnapSyncTests : MultiSyncModeSelectorTestsBase
    {
        public MultiSyncModeSelectorSnapSyncTests(bool needToWaitForHeaders) : base(needToWaitForHeaders)
        {
        }

        [Test]
        public void Simple_snap_sync()
        {
            Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeHasNeverSyncedBefore()
                .AndGoodPeersAreKnown()
                .WhenSnapSyncWithoutFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastSync);
        }

        [Test]
        public void Simple_snap_sync_with_fast_blocks()
        {
            // note that before we download at least one header we cannot start fast sync
            Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeHasNeverSyncedBefore()
                .AndGoodPeersAreKnown()
                .WhenSnapSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastHeaders);
        }

        [Test]
        public void Finished_fast_sync_but_not_snap_sync()
        {
            Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedFastBlocksAndFastSync()
                .AndGoodPeersAreKnown()
                .WhenSnapSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(SyncMode.SnapSync);
        }

        [Test]
        public void Finished_fast_sync_but_not_snap_sync_and_fast_blocks_in_progress()
        {
            Scenario.GoesLikeThis(_needToWaitForHeaders)
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenSnapSyncWithFastBlocksIsConfigured()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.SnapSync | SyncMode.FastHeaders));
        }

        [Test]
        public void Finished_snap_node_but_not_fast_blocks()
        {
            Scenario.GoesLikeThis(_needToWaitForHeaders)
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .WhenSnapSyncWithFastBlocksIsConfigured()
                .AndGoodPeersAreKnown()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.SnapSync | SyncMode.FastHeaders));
        }
    }
}
