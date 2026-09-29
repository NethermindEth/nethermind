// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class StateSyncPivotTest
{
    [TestCase(1000UL, 1000UL, 10UL, 100UL, 1000UL, 0UL)]
    [TestCase(900UL, 1000UL, 10UL, 50UL, 1000UL, 0UL)]
    [TestCase(900UL, 1000UL, 10UL, 100UL, 1000UL, 0UL)]
    [TestCase(900UL, 900UL, 32UL, 100UL, 900UL, 0UL)]
    [TestCase(0UL, 300UL, 32UL, 100UL, 301UL, 300UL)]
    public void Will_set_new_best_header_some_distance_from_best_suggested(
        ulong originalBestSuggested,
        ulong newBestSuggested,
        ulong minDistance,
        ulong maxDistance,
        ulong newPivotHeader,
        ulong syncPivot
    )
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<ulong>())
            .Returns(static (ci) => Build.A.BlockHeader.WithNumber(ci.ArgAt<ulong>(0)).TestObject);
        blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(true);

        Synchronization.FastSync.StateSyncPivot stateSyncPivot = new(blockTree,
            new TestSyncConfig()
            {
                PivotNumber = syncPivot,
                FastSync = true,
                StateMinDistanceFromHead = minDistance,
                StateMaxDistanceFromHead = maxDistance,
            }, LimboLogs.Instance);
        blockTree.SyncPivot = (syncPivot, Keccak.Zero);

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(originalBestSuggested).TestObject);
        Assert.That(stateSyncPivot.GetPivotHeader(), Is.Not.Null);

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(newBestSuggested).TestObject);
        Assert.That(stateSyncPivot.GetPivotHeader()?.Number, Is.EqualTo(newPivotHeader));
    }

    [Test]
    public void Will_resolve_a_new_pivot_when_the_current_one_is_reorged_out()
    {
        BlockHeader orphaned = Build.A.BlockHeader.WithNumber(100).WithStateRoot(TestItem.KeccakA).TestObject;
        BlockHeader canonical = Build.A.BlockHeader.WithNumber(100).WithStateRoot(TestItem.KeccakB).TestObject;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(100).TestObject);
        blockTree.FindHeader(Arg.Any<ulong>()).Returns(orphaned, canonical);
        blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(ci => ReferenceEquals(ci.ArgAt<BlockHeader>(0), canonical));

        Synchronization.FastSync.StateSyncPivot stateSyncPivot = new(blockTree,
            new TestSyncConfig()
            {
                FastSync = true,
                StateMinDistanceFromHead = 32,
                StateMaxDistanceFromHead = 128,
            }, LimboLogs.Instance);
        blockTree.SyncPivot = (0UL, Keccak.Zero);

        Assert.That(stateSyncPivot.GetPivotHeader(), Is.SameAs(orphaned));

        // Head has not moved far enough to trigger the distance-based refresh, so without the canonicity
        // check the reorged-out header would be handed out forever.
        Assert.That(stateSyncPivot.GetPivotHeader(), Is.SameAs(canonical));
    }

    // maxDistance is deliberately huge so GetPivotHeader's own re-pivot can never fire and every observed move is
    // attributable to the failure-streak path.
    private static Synchronization.FastSync.StateSyncPivot BuildPivot(IBlockTree blockTree, ulong minDistance = 32UL, ulong maxDistance = 100_000UL) =>
        new(blockTree, BuildSyncConfig(minDistance, maxDistance), LimboLogs.Instance);

    private static TestSyncConfig BuildSyncConfig(ulong minDistance = 32UL, ulong maxDistance = 100_000UL) =>
        new()
        {
            PivotNumber = 0UL,
            FastSync = true,
            SnapSyncAccountRangePartitionCount = 1,
            StateMinDistanceFromHead = minDistance,
            StateMaxDistanceFromHead = maxDistance,
        };

    // SnapSyncFeed.AnalyzeResponsePerPeer reaches the pivot only through ProgressTracker.UpdatePivot, which is where
    // the rate limit lives; these tests drive the composition rather than the pivot alone.
    private static ProgressTracker BuildTracker(Synchronization.FastSync.StateSyncPivot pivot, TestSyncConfig syncConfig) =>
        new(Substitute.For<ISnapTrieFactory>(), syncConfig, pivot, LimboLogs.Instance);

    private static IBlockTree BuildBlockTree()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<ulong>())
            .Returns(static (ci) => Build.A.BlockHeader.WithNumber(ci.ArgAt<ulong>(0)).TestObject);
        // GetPivotHeader re-pivots when the current pivot is not on the main chain; a bare substitute answers false,
        // which would make every read of the pivot move it and hide what these tests measure.
        blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(true);
        blockTree.SyncPivot = (0UL, Keccak.Zero);
        return blockTree;
    }

    // The regression itself (#13200): repeated streaks on a chain that produces a block or two between them must not
    // drag the pivot along with the head. Before the fix this loop moved the pivot on every single call.
    [Test]
    public void UpdatePivot_repeated_streaks_on_a_fast_chain_do_not_drag_the_pivot_with_the_head()
    {
        IBlockTree blockTree = BuildBlockTree();
        TestSyncConfig syncConfig = BuildSyncConfig();
        Synchronization.FastSync.StateSyncPivot pivot = BuildPivot(blockTree);
        using ProgressTracker tracker = BuildTracker(pivot, syncConfig);

        ulong head = 1000UL;
        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(head).TestObject);
        ulong original = pivot.GetPivotHeader()!.Number;

        int moves = 0;
        ulong previous = original;
        for (int streak = 0; streak < 10; streak++)
        {
            head += 2UL; // OP Mainnet: ~1.5 blocks between consecutive streaks
            blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(head).TestObject);
            tracker.UpdatePivot();

            ulong now = pivot.GetPivotHeader()!.Number;
            if (now != previous) moves++;
            previous = now;
        }

        Assert.That(moves, Is.LessThanOrEqualTo(1), "the pivot must not step with every streak; head advanced 20 blocks over 10 streaks");
    }

    // A pivot the peers have genuinely dropped still has to be replaceable; the guard only delays the move until the
    // head is far enough ahead for the new pivot to be different in a way that matters.
    [Test]
    public void UpdatePivot_still_recovers_from_a_stale_pivot_once_the_head_moves_on()
    {
        IBlockTree blockTree = BuildBlockTree();
        TestSyncConfig syncConfig = BuildSyncConfig();
        Synchronization.FastSync.StateSyncPivot pivot = BuildPivot(blockTree);
        using ProgressTracker tracker = BuildTracker(pivot, syncConfig);

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(1000UL).TestObject);
        ulong original = pivot.GetPivotHeader()!.Number;

        for (ulong head = 1001UL; head <= 1031UL; head++)
        {
            blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(head).TestObject);
            tracker.UpdatePivot();
            Assert.That(pivot.GetPivotHeader()!.Number, Is.EqualTo(original), $"suppressed while only {head - 1000UL} ahead");
        }

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(1032UL).TestObject);
        tracker.UpdatePivot();
        Assert.That(pivot.GetPivotHeader()!.Number, Is.EqualTo(1032UL));
    }

    // The head can sit behind the pivot - a reorg, or a pivot that TrySetNewBestHeader placed ahead of
    // BestSuggestedHeader. StateSyncPivot.Diff is a SaturatingSub for exactly this reason: a raw `head - pivot` in
    // ulong arithmetic underflows to a value far above StateMinDistanceFromHead, and the pivot would then move on
    // every streak, which is the livelock this fix exists to stop.
    [TestCase(1UL, TestName = "Head one block behind the pivot")]
    [TestCase(500UL, TestName = "Head far behind the pivot")]
    public void UpdatePivot_does_not_move_when_the_head_is_behind_the_pivot(ulong headRetreat)
    {
        IBlockTree blockTree = BuildBlockTree();
        TestSyncConfig syncConfig = BuildSyncConfig();
        Synchronization.FastSync.StateSyncPivot pivot = BuildPivot(blockTree);
        using ProgressTracker tracker = BuildTracker(pivot, syncConfig);

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(1000UL).TestObject);
        ulong original = pivot.GetPivotHeader()!.Number;

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(1000UL - headRetreat).TestObject);
        tracker.UpdatePivot();

        Assert.That(pivot.GetPivotHeader()!.Number, Is.EqualTo(original));
    }

    // TreeSync.ResetStateRootToBestSuggested calls UpdateHeaderForcefully at the start of every state sync round to
    // pick up the newest state root; that caller is not responding to a failure and must not be rate-limited, which
    // is why the guard sits in ProgressTracker.UpdatePivot rather than on the pivot itself.
    [TestCase(1UL)]
    [TestCase(2UL)]
    [TestCase(31UL)]
    public void UpdateHeaderForcefully_still_follows_the_head_by_a_single_block(ulong headAdvance)
    {
        IBlockTree blockTree = BuildBlockTree();
        Synchronization.FastSync.StateSyncPivot pivot = BuildPivot(blockTree);

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(1000UL).TestObject);
        ulong original = pivot.GetPivotHeader()!.Number;

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(1000UL + headAdvance).TestObject);
        pivot.UpdateHeaderForcefully();

        Assert.That(pivot.GetPivotHeader()!.Number, Is.EqualTo(original + headAdvance));
    }
}
