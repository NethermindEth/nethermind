// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TreeSyncTests
{
    private ITreeSyncStore _store = null!;
    private IStateSyncPivot _stateSyncPivot = null!;
    private TreeSync _treeSync = null!;
    private int _syncCompletedCount;

    [SetUp]
    public void Setup()
    {
        _store = Substitute.For<ITreeSyncStore>();
        _stateSyncPivot = Substitute.For<IStateSyncPivot>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.NetworkId.Returns(1ul);

        _treeSync = new TreeSync(
            new TestMemDb(),
            _store,
            blockTree,
            _stateSyncPivot,
            new SyncConfig(),
            LimboLogs.Instance);

        _syncCompletedCount = 0;
        _treeSync.SyncCompleted += (_, _) => _syncCompletedCount++;
    }

    [Test]
    public void VerifyPostSyncCleanUp_WhenPivotStateRootMatchesRootNode_FinalizesSync()
    {
        // 1. Active pivot points at state root A.
        // 2. ResetStateRootToBestSuggested sets _rootNode = A.
        // 3. Cleanup runs while pivot is still at A.
        // 4. FinalizeSync must be invoked with pivot A and SyncCompleted must fire.
        BlockHeader pivotA = BuildPivot(100, TestItem.KeccakA);
        _stateSyncPivot.GetPivotHeader().Returns(pivotA);
        _treeSync.ResetStateRootToBestSuggested(SyncFeedState.Dormant);
        _store.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>()); // precondition: FinalizeSync must not be called before cleanup runs

        _treeSync.VerifyPostSyncCleanUp();

        _store.Received(1).FinalizeSync(pivotA);
        _syncCompletedCount.Should().Be(1, "SyncCompleted must fire exactly once when state root matches");
    }

    [Test]
    public void VerifyPostSyncCleanUp_WhenPivotRotatedAfterReset_SkipsFinalizeSync()
    {
        // Issue #11457 regression guard. Previously TreeSync.SaveNode invoked
        // FinalizeSync with whatever GetPivotHeader returned at the moment of the
        // IsRoot save; if the pivot had rotated, FinalizeSync was called with a
        // pivot whose state root had no trie data, persisting a corrupted
        // CurrentState pointer in the FlatDB and stalling sync after restart.
        //
        // 1. Active pivot points at state root A; ResetStateRoot sets _rootNode = A.
        // 2. Pivot rotates to a different state root B (state root for which no
        //    trie data exists).
        // 3. Cleanup runs and observes pivotHeader.StateRoot != _rootNode.
        // 4. FinalizeSync must be skipped to prevent the corruption.
        BlockHeader pivotA = BuildPivot(100, TestItem.KeccakA);
        BlockHeader pivotB = BuildPivot(101, TestItem.KeccakB);
        _stateSyncPivot.GetPivotHeader().Returns(pivotA);
        _treeSync.ResetStateRootToBestSuggested(SyncFeedState.Dormant);
        _stateSyncPivot.GetPivotHeader().Returns(pivotB);
        _store.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>()); // precondition: FinalizeSync must not be called before cleanup runs

        _treeSync.VerifyPostSyncCleanUp();

        _store.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
        _syncCompletedCount.Should().Be(1, "SyncCompleted must still fire so the feed can transition out of state sync");
    }

    private static BlockHeader BuildPivot(long blockNumber, Hash256 stateRoot) =>
        Build.A.BlockHeader.WithNumber(blockNumber).WithStateRoot(stateRoot).TestObject;
}
