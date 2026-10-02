// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
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
    public async Task Concurrent_read_only_processing_uses_independent_worker_groups([Values] bool attached)
    {
        Block block = Build.A.Block.WithNumber(0).TestObject;
        ParallelUnbalancedWork.WorkerGroup original = attached ? new(2) : null;
        block.Workers = original;
        using CountdownEvent entered = new(2);
        using ManualResetEventSlim release = new();
        ConcurrentBag<ParallelUnbalancedWork.WorkerGroup> groups = [];
        Task Process() => Task.Run(() =>
        {
            IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
            blockProcessor.ProcessOne(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    groups.Add(ParallelUnbalancedWork.GetCurrentGroup());
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
                ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation, NullBlockTracer.Instance);
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
            Assert.That(observed, Has.None.SameAs(original));
            Assert.That(block.Workers, Is.SameAs(original));
        }
    }

    [Test]
    public void Hash_only_queue_reference_restores_the_worker_group()
    {
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        IBlockTree tree = container.Resolve<IBlockTree>();
        Block block = Build.A.Block.WithNumber(0).TestObject;
        tree.SuggestBlock(block, BlockTreeSuggestOptions.None);
        ParallelUnbalancedWork.WorkerGroup group = new(2);
        BlockRef reference = new(block.Hash!, ProcessingOptions.NoValidation, group);

        Assert.That(reference.Resolve(tree), Is.True);
        Assert.That(reference.Block!.Workers, Is.SameAs(group));
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
