// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.ParallelSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

[Parallelizable(ParallelScope.All)]
[TestFixture(false, BeaconSync.None)]
[TestFixture(false, BeaconSync.Headers)]
[TestFixture(false, BeaconSync.ControlMode)]
[TestFixture(true, BeaconSync.None)]
[TestFixture(true, BeaconSync.Headers)]
[TestFixture(true, BeaconSync.ControlMode)]
public class MultiSyncModeSelectorBeaconTests : MultiSyncModeSelectorTestsBase
{
    private readonly BeaconSync _mode;

    /// <summary>
    /// Test suite for beacon sync.
    /// </summary>
    /// <param name="needToWaitForHeaders">Do we need to wait for headers before other sync.</param>
    /// <param name="mode">Beacon mode we will be in.</param>
    public MultiSyncModeSelectorBeaconTests(bool needToWaitForHeaders, BeaconSync mode) : base(needToWaitForHeaders)
    {
        _mode = mode;
    }

    /// <summary>
    /// Change standard expectations based on beacon sync mode.
    /// </summary>
    /// <param name="baseExpectations">Sync mode expectations to change.</param>
    /// <returns>Enhanced expectations based on beacon sync mode.</returns>
    private SyncMode GetBeaconSyncExpectations(SyncMode baseExpectations)
    {
        // when beacon control mode, Disconnected, Fill, FastSync, StateNodes, SnapSync are ignored, instead we are waiting from block from beacon node
        baseExpectations = ChangeSyncMode(BeaconSync.ControlMode, baseExpectations, SyncMode.WaitingForBlock, true, SyncMode.Disconnected, SyncMode.Full, SyncMode.FastSync, SyncMode.StateNodes, SyncMode.SnapSync);

        // when beacon control mode, FastHeaders, FastBodies, FastReceipts, are run in parallel with waiting from block from beacon node
        baseExpectations = ChangeSyncMode(BeaconSync.ControlMode, baseExpectations, SyncMode.WaitingForBlock, false, SyncMode.FastHeaders, SyncMode.FastBodies, SyncMode.FastReceipts);

        // when beacon headers, WaitingForBlock, Fill, FastSync, StateNodes, SnapSync are ignored, instead we are syncing beacon headers
        baseExpectations = ChangeSyncMode(BeaconSync.Headers, baseExpectations, SyncMode.BeaconHeaders, true, SyncMode.WaitingForBlock, SyncMode.Full, SyncMode.FastSync, SyncMode.StateNodes, SyncMode.SnapSync);

        // when beacon headers, FastHeaders, FastBodies, FastReceipts, are run in parallel with beacon headers
        baseExpectations = ChangeSyncMode(BeaconSync.Headers, baseExpectations, SyncMode.BeaconHeaders, false, SyncMode.FastHeaders, SyncMode.FastBodies, SyncMode.FastReceipts);
        return baseExpectations;
    }

    /// <summary>
    /// Changes sync mode expectations.
    /// </summary>
    /// <param name="onMode">Only change on this mode.</param>
    /// <param name="expectations">Expectations to change.</param>
    /// <param name="add">Mode to add.</param>
    /// <param name="remove">Should remove <see cref="when"/> modes.</param>
    /// <param name="when">When <see cref="add"/> mode should be added, can be removed based on <see cref="remove"/>.</param>
    /// <returns>Changed expectations.</returns>
    private SyncMode ChangeSyncMode(BeaconSync onMode, SyncMode expectations, SyncMode add, bool remove, params SyncMode[] when)
    {
        if (_mode == onMode)
        {
            if (expectations == SyncMode.None)
            {
                expectations |= add;
            }
            else
            {
                foreach (SyncMode removeMode in when)
                {
                    if ((expectations & removeMode) != 0)
                    {
                        if (remove)
                        {
                            expectations &= ~removeMode;
                        }

                        expectations |= add;
                    }
                }
            }
        }

        return expectations;
    }

    [Test]
    public void Genesis_network()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeHasNeverSyncedBefore()
            .AndAPeerWithGenesisOnlyIsKnown()
            .ThenInAnySyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Disconnected));
    }

    [Test]
    public void Network_with_malicious_genesis()
    {
        // we will ignore the other node because its block is at height 0 (we never sync genesis only)
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeHasNeverSyncedBefore()
            .AndAPeerWithHighDiffGenesisOnlyIsKnown()
            .ThenInAnySyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Disconnected));
    }

    [Test]
    public void Empty_peers_or_no_connection()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .WhateverTheSyncProgressIs()
            .AndNoPeersAreKnown()
            .ThenInAnySyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Disconnected));
    }

    [Test]
    public void Disabled_sync()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .WhateverTheSyncProgressIs()
            .WhateverThePeerPoolLooks()
            .WhenSynchronizationIsDisabled()
            .ThenInAnySyncConfiguration()
            .TheSyncModeShouldBe(SyncMode.Disconnected);
    }

    [Test]
    public void Load_from_db()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .WhateverTheSyncProgressIs()
            .WhateverThePeerPoolLooks()
            .WhenThisNodeIsLoadingBlocksFromDb()
            .ThenInAnySyncConfiguration()
            .TheSyncModeShouldBe(SyncMode.DbLoad);
    }

    [Test]
    public void Simple_archive()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeHasNeverSyncedBefore()
            .AndGoodPeersAreKnown()
            .WhenFullArchiveSyncIsConfigured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full));
    }

    [Test]
    public void Simple_fast_sync()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeHasNeverSyncedBefore()
            .AndGoodPeersAreKnown()
            .When_FastSync_NoSnapSync_WithoutFastBlocks_Configured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.FastSync));
    }

    [Test]
    public void Simple_fast_sync_with_fast_blocks()
    {
        // note that before we download at least one header we cannot start fast sync
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeHasNeverSyncedBefore()
            .AndGoodPeersAreKnown()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.FastHeaders));
    }

    [Test]
    public void In_the_middle_of_fast_sync_with_fast_blocks()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
            .AndGoodPeersAreKnown()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(GetBeaconSyncExpectations(SyncMode.FastHeaders | SyncMode.FastSync)));
    }

    [Test]
    public void In_the_middle_of_fast_sync_with_fast_blocks_with_lesser_peers()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
            .AndPeersAreOnlyUsefulForFastBlocks()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.FastHeaders));
    }

    [Test]
    public void In_the_middle_of_fast_sync()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
            .AndGoodPeersAreKnown()
            .When_FastSync_NoSnapSync_WithoutFastBlocks_Configured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.FastSync));
    }

    [Test]
    public void In_the_middle_of_fast_sync_and_lesser_peers_are_known()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
            .AndPeersAreOnlyUsefulForFastBlocks()
            .When_FastSync_NoSnapSync_WithoutFastBlocks_Configured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.None));
    }

    [Test]
    public void Finished_fast_sync_but_not_state_sync_and_lesser_peers_are_known()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustFinishedFastBlocksAndFastSync()
            .AndPeersAreOnlyUsefulForFastBlocks()
            .When_FastSync_NoSnapSync_WithoutFastBlocks_Configured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.None));
    }

    [TestCase(FastBlocksState.None)]
    [TestCase(FastBlocksState.FinishedHeaders)]
    public void Finished_fast_sync_but_not_state_sync_and_lesser_peers_are_known_in_fast_blocks(FastBlocksState fastBlocksState)
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustFinishedFastBlocksAndFastSync(fastBlocksState)
            .AndPeersAreOnlyUsefulForFastBlocks()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(fastBlocksState.GetSyncMode()));
    }

    [Test]
    public void Finished_fast_sync_but_not_state_sync()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustFinishedFastBlocksAndFastSync()
            .AndGoodPeersAreKnown()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.StateNodes));
    }

    [Test]
    public void Finished_fast_sync_but_not_state_sync_and_fast_blocks_in_progress()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .ThisNodeFinishedFastSyncButNotFastBlocks()
            .AndGoodPeersAreKnown()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(GetBeaconSyncExpectations(SyncMode.StateNodes | SyncMode.FastHeaders)));
    }

    [Test]
    public void Finished_state_node_but_not_fast_blocks()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .ThisNodeFinishedFastSyncButNotFastBlocks()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .AndGoodPeersAreKnown()
            .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(GetBeaconSyncExpectations(SyncMode.StateNodes | SyncMode.FastHeaders)));
    }

    [TestCase(FastBlocksState.FinishedHeaders)]
    [TestCase(FastBlocksState.FinishedBodies)]
    [TestCase(FastBlocksState.FinishedReceipts)]
    public void Just_after_finishing_state_sync_and_fast_blocks(FastBlocksState fastBlocksState)
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustFinishedStateSyncAndFastBlocks(fastBlocksState)
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .AndGoodPeersAreKnown()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full | fastBlocksState.GetSyncMode(true)));
    }

    [TestCase(FastBlocksState.None)]
    [TestCase(FastBlocksState.FinishedHeaders)]
    [TestCase(FastBlocksState.FinishedBodies)]
    public void Just_after_finishing_state_sync_but_not_fast_blocks(FastBlocksState fastBlocksState)
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeFinishedStateSyncButNotFastBlocks(fastBlocksState)
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .AndGoodPeersAreKnown()
            .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(GetBeaconSyncExpectations(SyncMode.Full | fastBlocksState.GetSyncMode(true))));
    }

    [Test]
    public void When_finished_fast_sync_and_pre_pivot_block_appears()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeIsFullySynced()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .AndDesirablePrePivotPeerIsKnown()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.None));
    }

    [Test]
    public void When_fast_syncing_and_pre_pivot_block_appears()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeFinishedFastBlocksButNotFastSync()
            .AndDesirablePrePivotPeerIsKnown()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.None));
    }

    [Test]
    public void When_just_started_full_sync()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustStartedFullSyncProcessing()
            .AndGoodPeersAreKnown()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full));
    }

    [TestCase(FastBlocksState.None)]
    [TestCase(FastBlocksState.FinishedHeaders)]
    [TestCase(FastBlocksState.FinishedBodies)]
    [TestCase(FastBlocksState.FinishedReceipts)]
    public void When_just_started_full_sync_with_fast_blocks(FastBlocksState fastBlocksState)
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustStartedFullSyncProcessing(fastBlocksState)
            .AndGoodPeersAreKnown()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(GetBeaconSyncExpectations(SyncMode.Full | fastBlocksState.GetSyncMode(true))));
    }

    [Test]
    public void When_just_started_full_sync_and_peers_moved_forward()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustStartedFullSyncProcessing()
            .AndPeersMovedForward()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full));
    }

    [Description(
        "Fixes this scenario: // 2020-04-23 19:46:46.0143|INFO|180|Changing state to Full at processed:0|state:9930654|block:0|header:9930654|peer block:9930686 // 2020-04-23 19:46:47.0361|INFO|68|Changing state to StateNodes at processed:0|state:9930654|block:9930686|header:9930686|peer block:9930686")]
    [Test]
    public void When_just_started_full_sync_and_peers_moved_slightly_forward()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustStartedFullSyncProcessing()
            .AndPeersMovedSlightlyForward()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full));
    }

    [Test]
    public void When_recently_started_full_sync()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeRecentlyStartedFullSyncProcessing()
            .AndGoodPeersAreKnown()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full));
    }

    [Test]
    public void When_recently_started_full_sync_on_empty_clique_chain()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeRecentlyStartedFullSyncProcessingOnEmptyCliqueChain()
            .AndGoodPeersAreKnown()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full));
    }

    [Test]
    public void When_progress_is_corrupted()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfTheSyncProgressIsCorrupted()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .AndGoodPeersAreKnown()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.WaitingForBlock));
    }

    [Test]
    public void Waiting_for_processor()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .AndGoodPeersAreKnown()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.WaitingForBlock));
    }

    [Test]
    public void Can_switch_to_a_better_branch_while_processing()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .PeersFromDesirableBranchAreKnown()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full));
    }

    [Test]
    public void Can_switch_to_a_better_branch_while_full_synced()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeIsFullySynced()
            .PeersFromDesirableBranchAreKnown()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full));
    }

    [Test]
    public void Should_not_sync_when_synced_and_peer_reports_wrong_higher_total_difficulty()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeIsFullySynced()
            .PeersWithWrongDifficultyAreKnown()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.WaitingForBlock));
    }

    [Test]
    public void Fast_sync_catch_up()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeNeedsAFastSyncCatchUp()
            .AndGoodPeersAreKnown()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.FastSync));
    }

    [Test]
    public void Nearly_fast_sync_catch_up()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeNearlyNeedsAFastSyncCatchUp()
            .AndGoodPeersAreKnown()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full));
    }

    [Test]
    public void State_far_in_the_past()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeHasStateThatIsFarInThePast()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .AndGoodPeersAreKnown()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.StateNodes));
    }

    [Test]
    public void When_peers_move_slightly_forward_when_state_syncing()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustFinishedFastBlocksAndFastSync(FastBlocksState.FinishedHeaders)
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .AndPeersMovedSlightlyForward()
            .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(GetBeaconSyncExpectations(SyncMode.StateNodes | SyncMode.FastSync)));
    }

    [TestCase(FastBlocksState.None)]
    [TestCase(FastBlocksState.FinishedHeaders)]
    public void When_peers_move_slightly_forward_when_state_syncing(FastBlocksState fastBlocksState)
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustFinishedFastBlocksAndFastSync(fastBlocksState)
            .AndPeersMovedSlightlyForward()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(GetBeaconSyncExpectations(SyncMode.StateNodes | SyncMode.FastSync | fastBlocksState.GetSyncMode())));
    }

    [Test]
    public void When_state_sync_finished_but_needs_to_catch_up()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustFinishedStateSyncButNeedsToCatchUpToHeaders()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .AndGoodPeersAreKnown()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.StateNodes));
    }

    /// <summary>
    /// we DO NOT want the thing like below to happen (incorrectly go back to StateNodes from Full)
    /// 2020-04-25 19:58:32.1466|INFO|254|Changing state to Full at processed:0|state:9943624|block:0|header:9943624|peer block:9943656
    /// 2020-04-25 19:58:32.1466|INFO|254|Sync mode changed from StateNodes to Full
    /// 2020-04-25 19:58:33.1652|INFO|266|Changing state to StateNodes at processed:0|state:9943624|block:9943656|header:9943656|peer block:9943656
    /// </summary>
    [Test]
    public void When_state_sync_just_caught_up()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustFinishedStateSyncCatchUp()
            .AndGoodPeersAreKnown()
            .ThenInAnyFastSyncConfiguration()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.Full));
    }

    /// <summary>
    /// We should switch to State Sync in a case like below
    /// 2020-04-27 11:48:30.6691|Changing state to StateNodes at processed:2594949|state:2594949|block:2596807|header:2596807|peer block:2596807
    /// </summary>
    [Test]
    public void When_long_range_state_catch_up_is_needed()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustCameBackFromBeingOfflineForLongTimeAndFinishedFastSyncCatchUp()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .AndGoodPeersAreKnown()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.StateNodes));
    }

    [Test]
    public void Does_not_move_back_to_state_sync_mistakenly_when_in_full_sync_because_of_thinking_that_it_needs_to_catch_up()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfPeersMovedForwardBeforeThisNodeProcessedFirstFullBlock()
            .AndPeersMovedSlightlyForwardWithFastSyncLag()
            .When_FastSync_NoSnapSync_FastBlocks_Configured()
            .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(GetBeaconSyncExpectations(SyncMode.Full | SyncMode.FastHeaders)));
    }

    [Test]
    public void Simple_snap_sync()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeHasNeverSyncedBefore()
            .AndGoodPeersAreKnown()
            .WhenSnapSyncWithoutFastBlocksIsConfigured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.FastSync));
    }

    [Test]
    public void Simple_snap_sync_with_fast_blocks()
    {
        // note that before we download at least one header we cannot start fast sync
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeHasNeverSyncedBefore()
            .AndGoodPeersAreKnown()
            .WhenSnapSyncWithFastBlocksIsConfigured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.FastHeaders));
    }

    [Test]
    public void Finished_fast_sync_but_not_snap_sync()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .IfThisNodeJustFinishedFastBlocksAndFastSync()
            .AndGoodPeersAreKnown()
            .WhenSnapSyncWithFastBlocksIsConfigured()
            .TheSyncModeShouldBe(GetBeaconSyncExpectations(SyncMode.SnapSync));
    }

    [Test]
    public void Finished_fast_sync_but_not_snap_sync_and_fast_blocks_in_progress()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .ThisNodeFinishedFastSyncButNotFastBlocks()
            .AndGoodPeersAreKnown()
            .WhenSnapSyncWithFastBlocksIsConfigured()
            .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(GetBeaconSyncExpectations(SyncMode.SnapSync | SyncMode.FastHeaders)));
    }

    [Test]
    public void Finished_snap_node_but_not_fast_blocks()
    {
        Scenario.GoesLikeThis(_needToWaitForHeaders)
            .WhenInBeaconSyncMode(_mode)
            .ThisNodeFinishedFastSyncButNotFastBlocks()
            .WhenSnapSyncWithFastBlocksIsConfigured()
            .AndGoodPeersAreKnown()
            .TheSyncModeShouldBe(GetExpectationsIfNeedToWaitForHeaders(GetBeaconSyncExpectations(SyncMode.SnapSync | SyncMode.FastHeaders)));
    }
}
