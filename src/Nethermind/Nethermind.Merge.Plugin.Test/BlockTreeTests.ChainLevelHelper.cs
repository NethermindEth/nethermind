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

    /// <summary>
    /// When a block in the FastHeaders range is missing and FastHeaders is not active,
    /// the safety timer eventually forces a restart.
    /// </summary>
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
            .AssertNotForceNewBeaconSync()           // timer just started, not expired yet
            .AdvanceTime(TimeSpan.FromSeconds(31))
            .SuggestBlocksUsingChainLevels(20)
            .AssertForceNewBeaconSync();             // timer expired → restart
    }

    /// <summary>
    /// After a forced restart (ShouldForceStartNewSync set then cleared), the safety timer
    /// resets. A subsequent missing-header encounter starts a fresh timer.
    /// </summary>
    [Test]
    public void Safety_timer_resets_after_forced_restart()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconHeaders(8, 10)
            // Gap at 7: genuine gap in beacon range (7 >= LowestInsertedBeaconHeader=4) → immediate restart
            .WithSyncMode(SyncMode.None)
            .SuggestBlocksUsingChainLevels(20)
            .AssertForceNewBeaconSync()
            .ClearForceNewBeaconSync()
            .SuggestBlocksUsingChainLevels(20)
            .AssertForceNewBeaconSync();             // same genuine gap → restarts again immediately
    }

    /// <summary>
    /// When LowestInsertedBeaconHeader is above the missing block (beacon feed hasn't
    /// descended to that level yet), OnMissingBeaconHeader waits instead of restarting.
    /// </summary>
    [Test]
    public void Waits_when_beacon_headers_have_not_reached_block_yet()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertBeaconHeaders(8, 10)
            // LowestInsertedBeaconHeader = 8. Missing blocks 4-7 are below 8 → feed hasn't reached them.
            .WithSyncMode(SyncMode.None)
            .SuggestBlocksUsingChainLevels(20)
            .AssertNotForceNewBeaconSync();
    }

    /// <summary>
    /// When FastHeaders is actively running (SyncMode.FastHeaders set), gaps in the
    /// [0, SyncPivot] range are expected batch holes. Should wait, not restart.
    /// </summary>
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

    /// <summary>
    /// When both feeds are inactive and a block is missing in the beacon range
    /// (at or above LowestInsertedBeaconHeader), it's a genuine gap → immediate restart.
    /// </summary>
    [Test]
    public void Genuine_gap_below_beacon_frontier_forces_immediate_restart()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconHeaders(8, 10)
            // LowestInsertedBeaconHeader = 4. Block 7 is >= 4 → genuine gap.
            .WithSyncMode(SyncMode.None)
            .SuggestBlocksUsingChainLevels(20)
            .AssertForceNewBeaconSync();
    }

    /// <summary>
    /// Verifies the contiguous-range property: once LowestInsertedBeaconHeader = K,
    /// all blocks [K, BeaconPivot] must have ChainLevelInfo entries. If the full range
    /// is present, no restart is triggered.
    ///
    /// Documents the assumption that BeaconHeadersSyncFeed inserts contiguously
    /// via a dependency queue — no gaps between LowestInsertedBeaconHeader and
    /// the beacon pivot once the feed makes progress.
    /// </summary>
    [Test]
    public void No_restart_when_beacon_range_is_contiguous()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertBeaconHeaders(4, 10)
            // Full contiguous range [4, 11] — no gaps
            .WithSyncMode(SyncMode.None)
            .SuggestBlocksUsingChainLevels()
            .AssertNotForceNewBeaconSync();
    }
}
