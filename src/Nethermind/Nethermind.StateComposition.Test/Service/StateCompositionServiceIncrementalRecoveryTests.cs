// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
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
}
