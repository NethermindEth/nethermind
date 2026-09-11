// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
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

        public Harness() => Manager = new BlockAccessListManager(
            WorldState,
            LimboLogs.Instance,
            new BlocksConfig(), // ParallelExecution / ParallelExecutionBatchRead default to true
            Substitute.For<IWithdrawalProcessorFactory>(),
            new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance),
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

    public enum DrainOperation { Wait, Prepare, Dispose }

    [Test]
    public async Task Pending_warmup_is_drained_before_operation_returns([Values] DrainOperation operation)
    {
        Harness h = new();
        using GatedTaskScheduler scheduler = new();
        Task hint = Task.Factory.StartNew(static () => { }, CancellationToken.None, TaskCreationOptions.None, scheduler);
        h.IssueHint(hint);
        Task drain = Task.Factory.StartNew(() =>
        {
            switch (operation)
            {
                case DrainOperation.Wait: h.Manager.WaitForBalWarmup(); break;
                case DrainOperation.Prepare: h.IssueHint(Task.CompletedTask); break;
                case DrainOperation.Dispose: h.Manager.Dispose(); break;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Task first = await Task.WhenAny(scheduler.WaitEntered.Task, drain).WaitAsync(DrainTimeout);
            Assert.That(first, Is.SameAs(scheduler.WaitEntered.Task));
            Assert.That(drain.IsCompleted, Is.False);
        }
        finally
        {
            scheduler.Complete();
            await drain.WaitAsync(DrainTimeout);
        }
        Assert.That(hint.IsCompletedSuccessfully, Is.True);
        h.Manager.WaitForBalWarmup();
    }

    private sealed class GatedTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private Task? _queued;
        public TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void QueueTask(Task task) => _queued = task;
        protected override IEnumerable<Task>? GetScheduledTasks() => _queued is null ? [] : [_queued];

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            // GetResult tries to inline a queued task before blocking, proving the production wait was entered.
            WaitEntered.TrySetResult();
            _release.Wait();
            return TryExecuteTask(task);
        }

        public void Complete()
        {
            _release.Set();
            if (_queued is not null) TryExecuteTask(_queued);
        }

        public void Dispose() => _release.Dispose();
    }
}
