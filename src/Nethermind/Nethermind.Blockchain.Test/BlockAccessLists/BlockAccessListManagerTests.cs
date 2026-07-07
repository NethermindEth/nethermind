// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.BlockAccessLists;

[Parallelizable(ParallelScope.All)]
public class BlockAccessListManagerTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private sealed class Harness
    {
        public IWorldState WorldState { get; } = Substitute.For<IWorldState>();
        public BlockAccessListManager Manager { get; }

        public Harness() => Manager = ManualBlockAccessListManagerFactory.Create(
            WorldState,
            Substitute.For<ISpecProvider>(),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig(), // ParallelExecution / ParallelExecutionBatchRead default to true
            CodeInfoRepositoryFactories.Caching,
            // Enables parallel execution (and thus BAL read warmup), mirroring the production DI path.
            readOnlyTxProcessingEnvFactory: Substitute.For<IReadOnlyTxProcessingEnvFactory>());

        /// <summary>
        /// Stubs <see cref="IWorldState.HintBal"/> to return <paramref name="hint"/>, then runs
        /// <see cref="BlockAccessListManager.PrepareForProcessing"/> with prerequisites met so
        /// the hint gets tracked. Subsequent calls re-stub and re-prepare for a new block.
        /// </summary>
        public void IssueHint(Task hint)
        {
            WorldState.IsInScope.Returns(true);
            WorldState.HintBal(Arg.Any<ReadOnlyBlockAccessList>()).Returns(hint);

            IReleaseSpec spec = Substitute.For<IReleaseSpec>();
            spec.BlockLevelAccessListsEnabled.Returns(true);

            Block block = Build.A.Block
                .WithNumber(1) // not genesis — Enabled requires non-genesis
                .WithBlockAccessList(Build.A.BlockAccessList.TestObject)
                .TestObject;

            Manager.PrepareForProcessing(block, spec, ProcessingOptions.None);
        }
    }

    public enum HintState { NeverIssued, AlreadyCompleted, Canceled, Faulted }

    [TestCase(HintState.NeverIssued)]
    [TestCase(HintState.AlreadyCompleted)]
    [TestCase(HintState.Canceled)]
    [TestCase(HintState.Faulted)]
    public void WaitForBalWarmup_completes_without_throwing_for_non_pending_hint(HintState state)
    {
        Harness h = new();
        if (state != HintState.NeverIssued)
        {
            h.IssueHint(state switch
            {
                HintState.AlreadyCompleted => Task.CompletedTask,
                HintState.Canceled => Task.FromCanceled(new CancellationToken(canceled: true)),
                HintState.Faulted => Task.FromException(new InvalidOperationException("warm read failed")),
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            });
        }

        Task drain = Task.Run(h.Manager.WaitForBalWarmup);
        Assert.That(drain.Wait(DrainTimeout), Is.True);
        Assert.That(drain.Exception, Is.Null);
    }

    [Test]
    public void WaitForBalWarmup_blocks_until_pending_hint_completes()
    {
        Harness h = new();
        TaskCompletionSource hint = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.IssueHint(hint.Task);

        Task drain = Task.Run(h.Manager.WaitForBalWarmup);

        Assert.That(drain.Wait(TimeSpan.FromMilliseconds(50)), Is.False);
        hint.SetResult();
        Assert.That(drain.Wait(DrainTimeout), Is.True);
    }

    [Test]
    public void PrepareForProcessing_drops_hint_tracked_for_previous_block()
    {
        Harness h = new();
        TaskCompletionSource stale = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.IssueHint(stale.Task);

        // Re-prepare for a new block with an already-completed hint — the stale TCS is left
        // unsignaled. If PrepareForProcessing didn't drop it, drain would block on `stale`.
        h.IssueHint(Task.CompletedTask);

        Task drain = Task.Run(h.Manager.WaitForBalWarmup);
        Assert.That(drain.Wait(DrainTimeout), Is.True, "a stale hint from the previous block must not be awaited");
    }
}
