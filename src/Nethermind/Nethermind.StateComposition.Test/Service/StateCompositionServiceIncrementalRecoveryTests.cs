// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.StateComposition.Service;
using Nethermind.StateComposition.Snapshots;
using Nethermind.StateComposition.Test.Helpers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Service;

/// <summary>
/// Recovery-path tests for the incremental diff loop. When the baseline root is
/// missing from the trie DB (container was stopped longer than the pruning window
/// or pruning swept it while the plugin was idle), <see cref="StateCompositionService"/>
/// must detect the <see cref="MissingTrieNodeException"/>, invalidate the stale
/// baseline, and fire an auto-rescan instead of looping on <c>diff_errors</c>.
/// </summary>
[TestFixture]
public class StateCompositionServiceIncrementalRecoveryTests
{
    private static readonly Hash256 PrevRoot = Keccak.Compute("prev");
    private static readonly Hash256 NewRoot = Keccak.Compute("new");

    private static StateCompositionSnapshotStore CreateSnapshotStore() =>
        new(new MemDb(), LimboLogs.Instance);

    private static IStateCompositionConfig CreateConfig() =>
        TestDataBuilders.CreateTestConfig(trackDepthIncrementally: true);

    private static void SeedBaseline(StateCompositionStateHolder holder, long blockNumber, Hash256 stateRoot) =>
        holder.InitializeIncremental(TestDataBuilders.EmptyBaseline(), blockNumber, stateRoot);

    [Test]
    public void InvalidateBaseline_ClearsLastProcessedStateRoot_ButPreservesTrackers()
    {
        StateCompositionStateHolder holder = new();
        SeedBaseline(holder, blockNumber: 100, stateRoot: PrevRoot);

        holder.InvalidateBaseline();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(holder.LastProcessedStateRoot, Is.EqualTo(Hash256.Zero),
                "InvalidateBaseline must clear the baseline root so OnNewHeadBlock stops spawning diff tasks.");
            Assert.That(holder.HasIncrementalBaseline, Is.True,
                "Cached incremental stats must survive invalidation so RPC keeps serving last-known values.");
            Assert.That(holder.IncrementalBlock, Is.EqualTo(100),
                "Incremental block number must stay — only the root is deliberately dropped.");
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task RunIncrementalDiff_MissingTrieNode_InvalidatesBaselineAndSchedulesRescan()
    {
        long invalidationsBefore = Metrics.StateCompBaselineInvalidations;
        long diffErrorsBefore = Metrics.StateCompDiffErrors;
        long scansBefore = Metrics.StateCompScansCompleted;

        IStateReader stateReader = Substitute.For<IStateReader>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        StateCompositionStateHolder stateHolder = new();

        SeedBaseline(stateHolder, blockNumber: 100, stateRoot: PrevRoot);

        Block headBlock = Build.A.Block.WithNumber(101).WithStateRoot(NewRoot).TestObject;
        blockTree.Head.Returns(headBlock);
        blockTree.FindHeader(100, Arg.Any<BlockTreeLookupOptions>())
            .Returns(Build.A.BlockHeader.WithNumber(100).WithStateRoot(PrevRoot).TestObject);

        // Simulate pruned baseline: opening a read-only store for the diff throws
        // the exact exception TrieNode.ResolveNode raises when the root is gone.
        worldStateManager.CreateReadOnlyTrieStore()
            .Returns(_ => throw new MissingTrieNodeException(
                "stale root", null, TreePath.Empty, PrevRoot));

        using StateCompositionService service = new(
            stateReader, worldStateManager, blockTree, stateHolder,
            CreateSnapshotStore(), CreateConfig(), LimboLogs.Instance);

        service.RunIncrementalDiff();

        // Counter assertions are race-free: both bumps happen synchronously
        // inside the catch block before ScheduleBaselineRescan fires its Task.Run.
        // We deliberately DO NOT assert LastProcessedStateRoot==null here — the
        // rescan task may already have run and reseeded it. The holder-level
        // test covers that narrow behaviour directly.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompBaselineInvalidations, Is.EqualTo(invalidationsBefore + 1),
                "MissingTrieNodeException must bump the dedicated recovery counter.");
            Assert.That(Metrics.StateCompDiffErrors, Is.EqualTo(diffErrorsBefore),
                "Recoverable baseline invalidation must NOT count toward diff_errors.");
        }

        // ScheduleBaselineRescan is fire-and-forget. Against the substitute
        // IStateReader, AnalyzeAsync completes quickly — wait for it to reseed.
        await WaitForConditionAsync(
            () => stateHolder.HasScanBaseline,
            TimeSpan.FromSeconds(5),
            "Auto-rescan did not set HasScanBaseline=true within 5s").ConfigureAwait(false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompScansCompleted, Is.EqualTo(scansBefore + 1),
                "ScheduleBaselineRescan must have invoked AnalyzeAsync, which bumps scans_completed.");
            Assert.That(stateHolder.LastProcessedStateRoot, Is.Not.EqualTo(Hash256.Zero),
                "After the rescan, the baseline must be reseeded from the new header root.");
        }
    }

    [Test]
    public void RunIncrementalDiff_GenericException_KeepsLegacyErrorBehaviour()
    {
        long invalidationsBefore = Metrics.StateCompBaselineInvalidations;
        long diffErrorsBefore = Metrics.StateCompDiffErrors;

        IStateReader stateReader = Substitute.For<IStateReader>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        StateCompositionStateHolder stateHolder = new();

        SeedBaseline(stateHolder, blockNumber: 100, stateRoot: PrevRoot);

        Block headBlock = Build.A.Block.WithNumber(101).WithStateRoot(NewRoot).TestObject;
        blockTree.Head.Returns(headBlock);
        blockTree.FindHeader(100, Arg.Any<BlockTreeLookupOptions>())
            .Returns(Build.A.BlockHeader.WithNumber(100).WithStateRoot(PrevRoot).TestObject);

        worldStateManager.CreateReadOnlyTrieStore()
            .Returns(_ => throw new InvalidOperationException("boom"));

        using StateCompositionService service = new(
            stateReader, worldStateManager, blockTree, stateHolder,
            CreateSnapshotStore(), CreateConfig(), LimboLogs.Instance);

        service.RunIncrementalDiff();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompDiffErrors, Is.EqualTo(diffErrorsBefore + 1),
                "Non-MissingTrieNode failures still hit the generic error counter.");
            Assert.That(Metrics.StateCompBaselineInvalidations, Is.EqualTo(invalidationsBefore),
                "Generic failures must NOT fire the baseline-invalidation recovery path.");
            Assert.That(stateHolder.LastProcessedStateRoot, Is.EqualTo(PrevRoot),
                "Baseline must be intact — recovery is only for MissingTrieNodeException.");
        }
    }

    [Test]
    public void RunIncrementalDiff_HeadBehindBaseline_DefersWithoutInvalidating()
    {
        long invalidationsBefore = Metrics.StateCompBaselineInvalidations;
        long diffErrorsBefore = Metrics.StateCompDiffErrors;

        IStateReader stateReader = Substitute.For<IStateReader>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        StateCompositionStateHolder stateHolder = new();

        SeedBaseline(stateHolder, blockNumber: 100, stateRoot: PrevRoot);

        // Head is BEHIND the baseline (transient init state).
        Block staleHead = Build.A.Block.WithNumber(50).WithStateRoot(NewRoot).TestObject;
        blockTree.Head.Returns(staleHead);

        using StateCompositionService service = new(
            stateReader, worldStateManager, blockTree, stateHolder,
            CreateSnapshotStore(), CreateConfig(), LimboLogs.Instance);

        service.RunIncrementalDiff();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompBaselineInvalidations, Is.EqualTo(invalidationsBefore),
                "Deferred diff must NOT touch the baseline-invalidation counter.");
            Assert.That(Metrics.StateCompDiffErrors, Is.EqualTo(diffErrorsBefore),
                "Deferred diff must NOT touch the diff-errors counter.");
            Assert.That(stateHolder.LastProcessedStateRoot, Is.EqualTo(PrevRoot),
                "Baseline state root must be retained — the deferral leaves it untouched.");
            Assert.That(stateHolder.IncrementalBlock, Is.EqualTo(100),
                "Incremental block must stay at the baseline value while we wait.");
        }

        worldStateManager.DidNotReceive().CreateReadOnlyTrieStore();
        blockTree.DidNotReceive().FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>());
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task OnNewHeadBlock_NoBaseline_FiresDeferredBootstrapScan()
    {
        long scansBefore = Metrics.StateCompScansCompleted;

        IStateReader stateReader = Substitute.For<IStateReader>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        StateCompositionStateHolder stateHolder = new();

        Assert.That(stateHolder.LastProcessedStateRoot, Is.EqualTo(Hash256.Zero));

        Block headBlock = Build.A.Block.WithNumber(1).WithStateRoot(NewRoot).TestObject;
        blockTree.Head.Returns(headBlock);

        using StateCompositionService service = new(
            stateReader, worldStateManager, blockTree, stateHolder,
            CreateSnapshotStore(), CreateConfig(), LimboLogs.Instance);
        Assert.That(service, Is.Not.Null, "service must be constructed to subscribe NewHeadBlock");

        blockTree.NewHeadBlock += Raise.EventWith(blockTree, new BlockEventArgs(headBlock));

        await WaitForConditionAsync(
            () => stateHolder.HasScanBaseline,
            TimeSpan.FromSeconds(5),
            "Deferred bootstrap did not set HasScanBaseline=true within 5s").ConfigureAwait(false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompScansCompleted, Is.EqualTo(scansBefore + 1),
                "OnNewHeadBlock with lastRoot==Zero must dispatch AnalyzeAsync, which bumps scans_completed.");
            Assert.That(stateHolder.LastProcessedStateRoot, Is.Not.EqualTo(Hash256.Zero),
                "After the deferred bootstrap, the baseline must be seeded from the header root.");
        }
    }

    [Test]
    public void RunIncrementalDiff_OpensScopeOnBothPrevAndHeadBeforeAcquiringResolver()
    {
        long diffErrorsBefore = Metrics.StateCompDiffErrors;

        List<BlockHeader?> scopedHeaders = [];
        IReadOnlyTrieStore readOnlyStore = Substitute.For<IReadOnlyTrieStore>();
        readOnlyStore.BeginScope(Arg.Any<BlockHeader?>())
            .Returns(call =>
            {
                scopedHeaders.Add((BlockHeader?)call[0]);
                return Substitute.For<IDisposable>();
            });
        readOnlyStore.GetTrieStore(Arg.Any<Hash256?>())
            .Returns(_ =>
            {
                if (scopedHeaders.Count == 0)
                    throw new InvalidOperationException("BeginScope has not been called");
                throw new BeginScopeSentinel();
            });

        IStateReader stateReader = Substitute.For<IStateReader>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        worldStateManager.CreateReadOnlyTrieStore().Returns(readOnlyStore);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        StateCompositionStateHolder stateHolder = new();
        SeedBaseline(stateHolder, blockNumber: 100, stateRoot: PrevRoot);

        Block headBlock = Build.A.Block.WithNumber(101).WithStateRoot(NewRoot).TestObject;
        BlockHeader prevHeader = Build.A.BlockHeader.WithNumber(100).WithStateRoot(PrevRoot).TestObject;
        blockTree.Head.Returns(headBlock);
        blockTree.FindHeader(100, Arg.Any<BlockTreeLookupOptions>()).Returns(prevHeader);

        using StateCompositionService service = new(
            stateReader, worldStateManager, blockTree, stateHolder,
            CreateSnapshotStore(), CreateConfig(), LimboLogs.Instance);

        service.RunIncrementalDiff();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scopedHeaders, Has.Count.EqualTo(2),
                "Both prev and head must be scoped before any node resolution.");
            Assert.That(scopedHeaders[0], Is.SameAs(prevHeader),
                "Prev header must be scoped first so old-side reads land in the prev bundle.");
            Assert.That(scopedHeaders[1], Is.SameAs(headBlock.Header),
                "Head header must be scoped before the new-side resolver is acquired.");
            Assert.That(Metrics.StateCompDiffErrors, Is.EqualTo(diffErrorsBefore + 1),
                "BeginScopeSentinel propagates as a generic diff error — confirms the call site executed end-to-end.");
        }
    }

    [Test]
    public void RunIncrementalDiff_BeginScopeThrowsInvalidOperation_InvalidatesBaseline()
    {
        long invalidationsBefore = Metrics.StateCompBaselineInvalidations;
        long diffErrorsBefore = Metrics.StateCompDiffErrors;

        IReadOnlyTrieStore readOnlyStore = Substitute.For<IReadOnlyTrieStore>();
        readOnlyStore.BeginScope(Arg.Any<BlockHeader?>())
            .Returns(_ => throw new InvalidOperationException(
                "Unable to gather snapshots for state StateId { BlockNumber = 100, StateRoot = 0x... }."));

        IStateReader stateReader = Substitute.For<IStateReader>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        worldStateManager.CreateReadOnlyTrieStore().Returns(readOnlyStore);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        StateCompositionStateHolder stateHolder = new();

        SeedBaseline(stateHolder, blockNumber: 100, stateRoot: PrevRoot);
        Block headBlock = Build.A.Block.WithNumber(101).WithStateRoot(NewRoot).TestObject;
        blockTree.Head.Returns(headBlock);
        blockTree.FindHeader(100, Arg.Any<BlockTreeLookupOptions>())
            .Returns(Build.A.BlockHeader.WithNumber(100).WithStateRoot(PrevRoot).TestObject);

        using StateCompositionService service = new(
            stateReader, worldStateManager, blockTree, stateHolder,
            CreateSnapshotStore(), CreateConfig(), LimboLogs.Instance);

        service.RunIncrementalDiff();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompBaselineInvalidations, Is.EqualTo(invalidationsBefore + 1),
                "BeginScope's InvalidOperationException must route through the baseline-invalidation recovery path.");
            Assert.That(Metrics.StateCompDiffErrors, Is.EqualTo(diffErrorsBefore),
                "Recoverable bundle-gather failures must NOT count toward diff_errors.");
        }
    }

    [Test]
    public void RunIncrementalDiff_PrevHeaderMissing_InvalidatesBaselineAndRescans()
    {
        long invalidationsBefore = Metrics.StateCompBaselineInvalidations;
        long diffErrorsBefore = Metrics.StateCompDiffErrors;

        IStateReader stateReader = Substitute.For<IStateReader>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        StateCompositionStateHolder stateHolder = new();
        SeedBaseline(stateHolder, blockNumber: 100, stateRoot: PrevRoot);

        Block headBlock = Build.A.Block.WithNumber(101).WithStateRoot(NewRoot).TestObject;
        blockTree.Head.Returns(headBlock);
        blockTree.FindHeader(100, Arg.Any<BlockTreeLookupOptions>()).Returns((BlockHeader?)null);

        using StateCompositionService service = new(
            stateReader, worldStateManager, blockTree, stateHolder,
            CreateSnapshotStore(), CreateConfig(), LimboLogs.Instance);

        service.RunIncrementalDiff();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompBaselineInvalidations, Is.EqualTo(invalidationsBefore + 1),
                "Missing prev header must invalidate the baseline.");
            Assert.That(Metrics.StateCompDiffErrors, Is.EqualTo(diffErrorsBefore),
                "Missing prev header is a recoverable condition; it must NOT count as a diff error.");
        }
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        using CancellationTokenSource cts = new(timeout);
        try
        {
            while (!condition())
            {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(message);
        }
    }

    private sealed class BeginScopeSentinel : Exception;
}
