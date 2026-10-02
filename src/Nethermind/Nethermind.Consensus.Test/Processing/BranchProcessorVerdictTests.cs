// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Test;

namespace Nethermind.Consensus.Test.Processing;

public class BranchProcessorVerdictTests
{
    [Test]
    public async Task Concurrent_processing_uses_invocation_worker_groups([Values] bool readOnly, [Values] bool ambient)
    {
        Block block = Build.A.Block.WithNumber(0).TestObject;
        using CountdownEvent entered = new(2);
        using ManualResetEventSlim release = new();
        ConcurrentBag<ParallelUnbalancedWork.WorkerGroup> groups = [];
        Task Process() => Task.Run(() =>
        {
            ParallelUnbalancedWork.WorkerGroup original = ambient ? new(2) : null;
            using ParallelUnbalancedWork.WorkerScope workers = original?.Enter();
            IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
            blockProcessor.ProcessOne(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    ParallelUnbalancedWork.WorkerGroup current = ParallelUnbalancedWork.GetCurrentGroup();
                    Assert.That(current, ambient && !readOnly ? Is.SameAs(original) : Is.Not.SameAs(original));
                    groups.Add(current);
                    entered.Signal();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Processing was not released");
                    return (block, Array.Empty<TxReceipt>());
                });
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddSingleton(blockProcessor)
                .AddSingleton(Substitute.For<IBlockCachePreWarmer>())
                .Build();
            container.Resolve<IMainProcessingContext>().BranchProcessor.Process(null, [block],
                ProcessingOptions.NoValidation | (readOnly ? ProcessingOptions.ReadOnlyChain : ProcessingOptions.None), NullBlockTracer.Instance);
            Assert.That(ParallelUnbalancedWork.GetCurrentGroup(), Is.SameAs(original));
        });
        Task first = Process();
        Task second = Process();
        bool concurrent;
        try { concurrent = entered.Wait(TimeSpan.FromSeconds(10)); }
        finally { release.Set(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        ParallelUnbalancedWork.WorkerGroup[] observed = groups.ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(concurrent, Is.True, "independent calls must reach execution together");
            Assert.That(observed, Has.Length.EqualTo(2));
            Assert.That(observed, Is.All.Not.Null);
            Assert.That(observed[0], Is.Not.SameAs(observed[1]));
        }
    }

    [Test]
    public async Task Queue_transfers_the_invocation_group_to_preprocessing_and_execution([Values] bool hashOnly)
    {
        Block block = Build.A.Block.WithNumber(0).TestObject;
        ConcurrentBag<ParallelUnbalancedWork.WorkerGroup> preprocessing = [];
        ConcurrentBag<ParallelUnbalancedWork.WorkerGroup> execution = [];
        IBlockPreprocessorStep preprocessor = Substitute.For<IBlockPreprocessorStep>();
        preprocessor.When(step => step.RecoverDataForQueuedProcessing(Arg.Any<Block>()))
            .Do(_ => preprocessing.Add(ParallelUnbalancedWork.GetCurrentGroup()));
        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        branchProcessor.Process(Arg.Any<BlockHeader>(), Arg.Any<IReadOnlyList<Block>>(), Arg.Any<ProcessingOptions>(),
            Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                execution.Add(ParallelUnbalancedWork.GetCurrentGroup());
                return new[] { block };
            });
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddLast<IBlockPreprocessorStep>(_ => preprocessor)
            .AddSingleton(branchProcessor)
            .Build();
        IBlockTree tree = container.Resolve<IBlockTree>();
        tree.SuggestBlock(block, BlockTreeSuggestOptions.None);
        BlockchainProcessor queue = (BlockchainProcessor)container.Resolve<IMainProcessingContext>().BlockchainProcessor;
        if (hashOnly) queue.SoftMaxRecoveryQueueSizeInTx = 0;
        queue.Pause();
        queue.Start();
        ParallelUnbalancedWork.WorkerGroup[] groups = [new(2), new(2)];
        try
        {
            foreach (ParallelUnbalancedWork.WorkerGroup group in groups)
            {
                ValueTask enqueue;
                using (group.Enter()) enqueue = queue.Enqueue(block, ProcessingOptions.ForceProcessing | ProcessingOptions.ReadOnlyChain);
                await enqueue;
            }
            Task removed = queue.WaitUntilRemovedAsync(block.Hash!).AsTask();
            queue.Resume();
            await removed.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(execution, Is.EquivalentTo(groups));
                Assert.That(preprocessing, Does.Contain(groups[0]).And.Contain(groups[1]));
                Assert.That(preprocessing, Has.None.Null);
                Assert.That(ParallelUnbalancedWork.GetCurrentGroup(), Is.Null);
            }
        }
        finally
        {
            queue.Resume();
            await queue.StopAsync();
        }
    }

    /// <summary>
    /// A block that fails after its verdict keeps the invalid-block handling unless a request was actually answered
    /// VALID for it: only then does the failure belong to the commit rather than to the block. Sync and every other
    /// caller nobody answered for must still see the block as invalid.
    /// </summary>
    [TestCase(false, typeof(InvalidBlockException), TestName = "Process_FailsAfterUnansweredVerdict_KeepsInvalidBlock")]
    [TestCase(true, typeof(InvalidOperationException), TestName = "Process_FailsAfterAnsweredVerdict_ReportsCommitFailure")]
    public void Process_failure_after_the_verdict_is_a_commit_failure_only_once_answered(bool answered, Type expected)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;

        IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
        blockProcessor.ProcessOne(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>())
            .Returns((block, Array.Empty<TxReceipt>()));
        IWorldState worldState = Substitute.For<IWorldState>();
        worldState.TryBeginScopeAtTarget(Arg.Any<BlockHeader>(), out Arg.Any<IDisposable>())
            .Returns(call => call.Succeed(1, Substitute.For<IDisposable>()));

        BranchProcessor branchProcessor = new(blockProcessor, Substitute.For<ISpecProvider>(), worldState, Substitute.For<IBlockhashProvider>(),
            Substitute.For<IInclusionListSatisfactionChecker>(), LimboLogs.Instance);
        branchProcessor.BlockExecuted += (_, e) => e.Answered = answered;
        branchProcessor.BlockProcessed += (_, _) => throw new InvalidBlockException(block, "failed after the verdict");

        Exception thrown = Assert.Catch(() => branchProcessor.Process(parent, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance));

        Assert.That(thrown, Is.TypeOf(expected));
    }
}
