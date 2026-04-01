// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Synchronization.ParallelSync;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class BlockTreeTests
{
    [Test]
    public void Can_sync_using_chain_levels()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
            .SuggestBlocksUsingChainLevels()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9);
    }

    [Test]
    public void Can_sync_using_chain_levels_with_restart()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
            .Restart()
            .SuggestBlocksUsingChainLevels()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9);
    }

    [Test]
    public void Correct_levels_after_chain_level_sync()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
            .SuggestBlocksUsingChainLevels()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9, 10000000)
            .AssertChainLevel(0, 9)
            .AssertNotForceNewBeaconSync();
    }

    [Test]
    public void Correct_levels_after_chain_level_sync_with_nullable_td()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6, BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null)
            .InsertBeaconBlocks(8, 9, BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null)
            .SuggestBlocksUsingChainLevels()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9, 10000000)
            .AssertChainLevel(0, 9);
    }

    [Test]
    public void Correct_levels_after_chain_level_sync_with_zero_td()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6, BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Zero)
            .InsertBeaconBlocks(8, 9, BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Zero)
            .SuggestBlocksUsingChainLevels()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9, 10000000)
            .AssertChainLevel(0, 9);
    }

    [Test]
    public void Correct_levels_with_chain_fork()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
            .InsertFork(1, 9)
            .AssertBestSuggestedBody(3)
            .SuggestBlocksUsingChainLevels()
            .AssertBestSuggestedBody(9)
            .AssertChainLevel(0, 9);
    }

    // Gap at block 7 — InsertBeaconHeaders creates a non-contiguous range that the real
    // BeaconHeadersSyncFeed cannot produce (it uses a dependency queue for contiguous insertion).
    // This tests ChainLevelHelper's defensive behavior against gaps from any cause
    // (node restart, DB corruption, etc).
    [Test]
    public void Correct_levels_after_chain_level_sync_with_disconnected_beacon_chain()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconHeaders(8, 10)
            .SuggestBlocksUsingChainLevels(20)
            .AssertChainLevel(0, 4)
            .AssertForceNewBeaconSync();
    }

    // =====================================================================
    // Feed-aware OnMissingBeaconHeader guard tests
    // =====================================================================

    [Test]
    public void Safety_timer_forces_restart_when_expired_in_fast_headers_range()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertSyncPivotWithHeader(6)
            .InsertBeaconHeaders(7, 10)
            // Gap: blocks 4-5 missing in FastHeaders range [0, 6], feed not active
            .WithSyncMode(SyncMode.None)
            .SuggestBlocksUsingChainLevels(20)
            .AssertNotForceNewBeaconSync()           // timer just started
            .AdvanceTime(TimeSpan.FromSeconds(31))
            .SuggestBlocksUsingChainLevels(20)
            .AssertForceNewBeaconSync();             // timer expired
    }

    [Test]
    public void Waits_when_block_is_in_beacon_range()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .EnsureBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertBeaconHeaders(8, 10)
            // Missing blocks 4-7 are in [PivotDestinationNumber, PivotNumber] → wait
            .WithSyncMode(SyncMode.None)
            .SuggestBlocksUsingChainLevels(20)
            .AssertNotForceNewBeaconSync();
    }

    [Test]
    public void Waits_when_fast_headers_active_and_block_missing_in_range()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertSyncPivotWithHeader(6)
            .InsertBeaconHeaders(7, 10)
            // Blocks 4-5 missing in FastHeaders range, but feed is active
            .WithSyncMode(SyncMode.FastHeaders)
            .SuggestBlocksUsingChainLevels(20)
            .AssertNotForceNewBeaconSync();
    }

    [Test]
    public void Genuine_gap_in_beacon_range_forces_restart_after_timer()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .EnsureBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconHeaders(8, 10)
            // Block 7 missing in beacon range — guard waits, safety timer catches it
            .WithSyncMode(SyncMode.None)
            .SuggestBlocksUsingChainLevels(20)
            .AssertNotForceNewBeaconSync()
            .AdvanceTime(TimeSpan.FromSeconds(31))
            .SuggestBlocksUsingChainLevels(20)
            .AssertForceNewBeaconSync();
    }

    // =====================================================================
    // Additional behavioral tests
    // =====================================================================

    [Test]
    public void No_restart_when_process_destination_is_null()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            // ProcessDestination not set — guard returns immediately
            .InsertBeaconHeaders(8, 10)
            .WithSyncMode(SyncMode.None)
            .SuggestBlocksUsingChainLevels(20)
            .AssertNotForceNewBeaconSync();
    }

    [Test]
    public void Missing_block_at_sync_pivot_boundary_goes_to_fast_headers_handler()
    {
        // Block exactly at SyncPivot.BlockNumber (N) goes to HandleMissingInFastHeadersRange (<=)
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .SetProcessDestination(11)
            .SetSyncPivotMetadataOnly(6)
            .InsertBeaconHeaders(7, 10)
            // Block 6 = SyncPivot, missing — handled by FastHeaders range.
            // FastHeaders active → wait
            .WithSyncMode(SyncMode.FastHeaders)
            .SuggestBlocksUsingChainLevels(20)
            .AssertNotForceNewBeaconSync();
    }

    [Test]
    public void Safety_timer_does_not_reset_on_repeated_wait_calls()
    {
        // Verifies that _waitStartedAt ??= doesn't restart the timer on each call.
        // Advance to just under the timeout, call twice more — should NOT expire.
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertSyncPivotWithHeader(6)
            .InsertBeaconHeaders(7, 10)
            // Gap at blocks 4-5, FastHeaders not active
            .WithSyncMode(SyncMode.None)
            .SuggestBlocksUsingChainLevels(20)       // starts timer at T=0
            .AssertNotForceNewBeaconSync()
            .AdvanceTime(TimeSpan.FromSeconds(25))   // T=25
            .SuggestBlocksUsingChainLevels(20)       // if timer reset, new T=0
            .AssertNotForceNewBeaconSync()
            .AdvanceTime(TimeSpan.FromSeconds(6))    // T=31 from original start (but T=6 if reset)
            .SuggestBlocksUsingChainLevels(20)
            .AssertForceNewBeaconSync();             // expires at 31s from ORIGINAL start, not from reset
    }

    [Test]
    public void Beacon_range_safety_timer_forces_restart_when_expired()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .EnsureBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertBeaconHeaders(8, 10)
            // Missing blocks 4-7 in beacon range [PivotDestinationNumber, 11]
            .WithSyncMode(SyncMode.None)
            .SuggestBlocksUsingChainLevels(20)
            .AssertNotForceNewBeaconSync()           // timer started
            .AdvanceTime(TimeSpan.FromSeconds(31))
            .SuggestBlocksUsingChainLevels(20)
            .AssertForceNewBeaconSync();             // timer expired
    }
}
