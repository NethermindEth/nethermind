// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.ParallelSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture(false)]
    [TestFixture(true)]
    public class MultiSyncModeSelectorFastSyncTests(bool needToWaitForHeaders) : MultiSyncModeSelectorTestsBase(needToWaitForHeaders)
    {
        [Test]
        public void Genesis_network() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeHasNeverSyncedBefore()
                .AndAPeerWithGenesisOnlyIsKnown()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Disconnected);

        [Test]
        public void Network_with_malicious_genesis() =>
            // we will ignore the other node because its block is at height 0 (we never sync genesis only)
            Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeHasNeverSyncedBefore()
                .AndAPeerWithHighDiffGenesisOnlyIsKnown()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Disconnected);

        [Test]
        public void Empty_peers_or_no_connection() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .WhateverTheSyncProgressIs()
                .AndNoPeersAreKnown()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Disconnected);

        [Test]
        public void Disabled_sync() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .WhateverTheSyncProgressIs()
                .WhateverThePeerPoolLooks()
                .WhenSynchronizationIsDisabled()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Disconnected);

        [Test]
        public void Load_from_db() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .WhateverTheSyncProgressIs()
                .WhateverThePeerPoolLooks()
                .WhenThisNodeIsLoadingBlocksFromDb()
                .ThenInAnySyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.DbLoad);

        [Test]
        public void Load_from_without_merge_sync_pivot_resolved() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .WhenMergeSyncPivotNotResolvedYet()
                .WhateverThePeerPoolLooks()
                .WhenThisNodeIsLoadingBlocksFromDb()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.DbLoad | SyncMode.UpdatingPivot);

        [Test]
        public void Simple_archive() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeHasNeverSyncedBefore()
                .AndGoodPeersAreKnown()
                .WhenFullArchiveSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.Full);

        [Test]
        public void Simple_fast_sync() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks(FastBlocksState.FinishedHeaders)
                .AndGoodPeersAreKnown()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(SyncMode.FastSync);

        [Test]
        public void Simple_snap_sync() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks(FastBlocksState.FinishedHeaders)
                .AndGoodPeersAreKnown()
                .WhenSnapSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastSync);

        [Test]
        public void If_SnapSyncDisabled_RangesNotFinished_StateNodes() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks(FastBlocksState.FinishedHeaders)
                .AndGoodPeersAreKnown()
                .WhenSnapSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastSync);

        [Test]
        public void Simple_fast_sync_with_fast_blocks() =>
            // note that before we download at least one header we cannot start fast sync
            Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeHasNeverSyncedBefore()
                .AndGoodPeersAreKnown()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(SyncMode.FastHeaders);

        [Test]
        public void Simple_snap_sync_with_fast_blocks() =>
            // note that before we download at least one header we cannot start fast sync
            Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeHasNeverSyncedBefore()
                .AndGoodPeersAreKnown()
                .WhenSnapSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.FastHeaders);

        [Test]
        public void In_the_middle_of_fast_sync_with_fast_blocks() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
                .AndGoodPeersAreKnown()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.FastHeaders | SyncMode.FastSync));

        [Test]
        public void In_the_middle_of_fast_sync_with_fast_blocks_with_lesser_peers() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
                .AndPeersAreOnlyUsefulForFastBlocks()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(SyncMode.FastHeaders);

        [Test]
        public void In_the_middle_of_fast_sync() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks(FastBlocksState.FinishedHeaders)
                .AndGoodPeersAreKnown()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(SyncMode.FastSync);

        [Test]
        public void In_the_middle_of_fast_sync_and_lesser_peers_are_known() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks(FastBlocksState.FinishedHeaders)
                .AndPeersAreOnlyUsefulForFastBlocks()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(SyncMode.None);

        [Test]
        public void Finished_fast_sync_but_not_state_sync_and_lesser_peers_are_known() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedFastBlocksAndFastSync()
                .AndPeersAreOnlyUsefulForFastBlocks()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(SyncMode.None);

        [TestCase(FastBlocksState.None)]
        [TestCase(FastBlocksState.FinishedHeaders)]
        public void Finished_fast_sync_but_not_state_sync_and_lesser_peers_are_known_in_fast_blocks(FastBlocksState fastBlocksState) => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedFastBlocksAndFastSync(fastBlocksState)
                .AndPeersAreOnlyUsefulForFastBlocks()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(fastBlocksState.GetSyncMode());

        [Test]
        public void Finished_fast_sync_but_not_state_sync() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedFastBlocksAndFastSync()
                .AndGoodPeersAreKnown()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(SyncMode.StateNodes);

        [Test]
        public void Finished_fast_sync_but_not_snap_ranges() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedFastBlocksAndFastSync()
                .AndGoodPeersAreKnown()
                .WhenSnapSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.SnapSync);

        [Test]
        public void Finished_fast_sync_but_not_snap_ranges_IsFarFromHead() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedFastBlocksAndFastSync(bestHeader: Scenario.ChainHead.Number - 1000)
                .AndGoodPeersAreKnown()
                .WhenSnapSyncIsConfigured()
                .WhenHeaderIsFarFromHead()
                .TheSyncModeShouldBe(SyncMode.FastSync);

        [Test]
        public void Finished_fast_sync_and_snap_ranges() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedFastBlocksAndFastSync(snapRangesFinished: true)
                .AndGoodPeersAreKnown()
                .WhenSnapSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes);

        [Test]
        public void Finished_fast_sync_via_fast_sync_lag_but_not_state_sync() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .WhenBeaconProcessDestinationWithinFastSyncLag()
                .AndGoodPeersAreKnown()
                .WhenFastSyncIsConfigured()
                .TheSyncModeShouldBe(SyncMode.StateNodes);

        [Test]
        public void Finished_fast_sync_but_not_state_sync_and_fast_blocks_in_progress() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .AndGoodPeersAreKnown()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.StateNodes | SyncMode.FastHeaders));

        [Test]
        public void Finished_fast_sync_but_not_snap_sync_and_fast_blocks_in_progress() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .AndGoodPeersAreKnown()
                .WhenSnapSyncIsConfigured()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.SnapSync | SyncMode.FastHeaders));

        [Test]
        public void Finished_state_node_but_not_fast_blocks() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .When_FastSync_NoSnapSync_Configured()
                .AndGoodPeersAreKnown()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.StateNodes | SyncMode.FastHeaders));

        [Test]
        public void Finished_snap_node_but_not_fast_blocks() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .ThisNodeFinishedFastSyncButNotFastBlocks()
                .WhenSnapSyncIsConfigured()
                .AndGoodPeersAreKnown()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.SnapSync | SyncMode.FastHeaders));

        [Test]
        public void Finished_any_sync_far_time_ago() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeHasBeenOfflineForLongTime()
                .WhenSnapSyncIsConfigured()
                .AndGoodPeersAreKnown()
                .TheSyncModeShouldBe(SyncMode.Full);

        [TestCase(FastBlocksState.FinishedHeaders)]
        [TestCase(FastBlocksState.FinishedBodies)]
        [TestCase(FastBlocksState.FinishedReceipts)]
        public void Just_after_finishing_state_sync_and_fast_blocks(FastBlocksState fastBlocksState) => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedStateSyncAndFastBlocks(fastBlocksState)
                .When_FastSync_NoSnapSync_Configured()
                .AndGoodPeersAreKnown()
                .TheSyncModeShouldBe(SyncMode.Full | fastBlocksState.GetSyncMode(true));

        [TestCase(FastBlocksState.None)]
        [TestCase(FastBlocksState.FinishedHeaders)]
        [TestCase(FastBlocksState.FinishedBodies)]
        public void Just_after_finishing_state_sync_but_not_fast_blocks(FastBlocksState fastBlocksState) => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeFinishedStateSyncButNotFastBlocks(fastBlocksState)
                .When_FastSync_NoSnapSync_Configured()
                .AndGoodPeersAreKnown()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.Full | fastBlocksState.GetSyncMode(true)));

        [Test]
        public void When_finished_fast_sync_and_pre_pivot_block_appears() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsFullySynced()
                .When_FastSync_NoSnapSync_Configured()
                .AndDesirablePrePivotPeerIsKnown()
                .TheSyncModeShouldBe(SyncMode.None);

        [Test]
        public void When_fast_syncing_and_pre_pivot_block_appears() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeFinishedFastBlocksButNotFastSync()
                .AndDesirablePrePivotPeerIsKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.None);

        [Test]
        public void When_just_started_full_sync() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustStartedFullSyncProcessing()
                .AndGoodPeersAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);

        [TestCase(FastBlocksState.None)]
        [TestCase(FastBlocksState.FinishedHeaders)]
        [TestCase(FastBlocksState.FinishedBodies)]
        [TestCase(FastBlocksState.FinishedReceipts)]
        public void When_just_started_full_sync_with_fast_blocks(FastBlocksState fastBlocksState) => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustStartedFullSyncProcessing(fastBlocksState)
                .AndGoodPeersAreKnown()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.Full | fastBlocksState.GetSyncMode(true)));

        [Test]
        public void When_just_started_full_sync_and_peers_moved_forward() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustStartedFullSyncProcessing()
                .AndPeersMovedForward()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);

        [Description("Fixes this scenario: // 2020-04-23 19:46:46.0143|INFO|180|Changing state to Full at processed:0|state:9930654|block:0|header:9930654|peer block:9930686 // 2020-04-23 19:46:47.0361|INFO|68|Changing state to StateNodes at processed:0|state:9930654|block:9930686|header:9930686|peer block:9930686")]
        [Test]
        public void When_just_started_full_sync_and_peers_moved_slightly_forward() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustStartedFullSyncProcessing()
                .AndPeersMovedSlightlyForward()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);

        [Test]
        public void When_recently_started_full_sync() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeRecentlyStartedFullSyncProcessing()
                .AndGoodPeersAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);

        [Test]
        public void When_recently_started_full_sync_on_empty_clique_chain() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeRecentlyStartedFullSyncProcessingOnEmptyCliqueChain()
                .AndGoodPeersAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);

        [Test]
        public void When_progress_is_corrupted() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfTheSyncProgressIsCorrupted()
                .When_FastSync_NoSnapSync_Configured()
                .AndGoodPeersAreKnown()
                .TheSyncModeShouldBe(SyncMode.WaitingForBlock);

        [Test]
        public void Waiting_for_processor() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync()
                .When_FastSync_NoSnapSync_Configured()
                .AndGoodPeersAreKnown()
                .TheSyncModeShouldBe(SyncMode.WaitingForBlock);

        [Test]
        public void Can_switch_to_a_better_branch_while_processing() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync()
                .When_FastSync_NoSnapSync_Configured()
                .PeersFromDesirableBranchAreKnown()
                .TheSyncModeShouldBe(SyncMode.Full);

        [Test]
        public void Can_switch_to_a_better_branch_while_full_synced() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsFullySynced()
                .PeersFromDesirableBranchAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);

        [Test]
        public void Should_not_sync_when_synced_and_peer_reports_wrong_higher_total_difficulty() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeIsFullySynced()
                .PeersWithWrongDifficultyAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.WaitingForBlock);

        [Test]
        public void State_far_in_the_past() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeHasStateThatIsFarInThePast()
                .When_FastSync_NoSnapSync_Configured()
                .AndGoodPeersAreKnown()
                .TheSyncModeShouldBe(SyncMode.StateNodes);

        [Test]
        public void When_peers_move_slightly_forward_when_state_syncing() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedFastBlocksAndFastSync(FastBlocksState.FinishedHeaders)
                .When_FastSync_NoSnapSync_Configured()
                .AndPeersMovedSlightlyForward()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.StateNodes | SyncMode.FastSync));

        [TestCase(FastBlocksState.None)]
        [TestCase(FastBlocksState.FinishedHeaders)]
        public void When_peers_move_slightly_forward_when_state_syncing(FastBlocksState fastBlocksState) => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedFastBlocksAndFastSync(fastBlocksState)
                .AndPeersMovedSlightlyForward()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.StateNodes | SyncMode.FastSync | fastBlocksState.GetSyncMode()));

        /// <summary>
        /// we DO NOT want the thing like below to happen (incorrectly go back to StateNodes from Full)
        /// 2020-04-25 19:58:32.1466|INFO|254|Changing state to Full at processed:0|state:9943624|block:0|header:9943624|peer block:9943656
        /// 2020-04-25 19:58:32.1466|INFO|254|Sync mode changed from StateNodes to Full
        /// 2020-04-25 19:58:33.1652|INFO|266|Changing state to StateNodes at processed:0|state:9943624|block:9943656|header:9943656|peer block:9943656
        /// </summary>
        [Test]
        public void When_state_sync_just_caught_up() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeJustFinishedStateSyncCatchUp()
                .AndGoodPeersAreKnown()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.Full);

        [Test]
        public void When_node_has_been_offline_for_long_time_stays_in_full_sync() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfThisNodeHasBeenOfflineForLongTime()
                .When_FastSync_NoSnapSync_Configured()
                .AndGoodPeersAreKnown()
                .TheSyncModeShouldBe(SyncMode.Full);

        [Test]
        public void Does_not_move_back_to_state_sync_mistakenly_when_in_full_sync_because_of_thinking_that_it_needs_to_catch_up() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfPeersMovedForwardBeforeThisNodeProcessedFirstFullBlock()
                .AndPeersMovedSlightlyForwardWithFastSyncLag()
                .When_FastSync_NoSnapSync_Configured()
                .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(SyncMode.Full | SyncMode.FastHeaders));

        [Test]
        public void When_state_sync_does_not_finished_then_sync_mode_should_not_be_full() => Scenario.GoesLikeThis(_needToWaitForHeaders)
                .IfTheNodeDoesNotFinishStateSync()
                .AndPeersMovedSlightlyForward()
                .ThenInAnyFastSyncConfiguration()
                .TheSyncModeShouldBe(SyncMode.StateNodes);

    }
}
