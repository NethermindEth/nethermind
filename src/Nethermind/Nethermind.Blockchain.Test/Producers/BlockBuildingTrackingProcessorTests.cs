// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.Tracing;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

public class BlockBuildingTrackingProcessorTests
{
    [TestCase(false, TestName = "The building flag is raised while the built block executes and cleared after")]
    [TestCase(true, TestName = "The building flag is cleared when executing the built block throws")]
    public void Building_flag_brackets_the_built_block_execution(bool processorThrows)
    {
        BlockBuildingTracker tracker = new();
        bool flagDuringExecution = false;
        IBlockchainProcessor inner = Substitute.For<IBlockchainProcessor>();
        inner.Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                flagDuringExecution = tracker.IsBuildingBlock;
                return processorThrows ? throw new InvalidOperationException("execution failed") : call.Arg<Block>();
            });
        BlockBuildingTrackingProcessor processor = new(inner, tracker);

        Action build = () => processor.Process(Build.A.Block.TestObject, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);
        if (processorThrows) Assert.Throws<InvalidOperationException>(build);
        else build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flagDuringExecution, Is.True, "gossiped validation cannot yield to a build it does not see");
            Assert.That(tracker.IsBuildingBlock, Is.False, "a flag left raised would defer gossip until the next build");
        }
    }

    [Test]
    public void Building_flag_stays_raised_until_the_last_overlapping_build_ends()
    {
        BlockBuildingTracker tracker = new();

        IDisposable first = tracker.BeginBlockBuilding();
        IDisposable second = tracker.BeginBlockBuilding();
        first.Dispose();
        first.Dispose();
        bool raisedWhileOneBuildRuns = tracker.IsBuildingBlock;
        second.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raisedWhileOneBuildRuns, Is.True, "the first build to finish, even disposed twice, must not clear the other's flag");
            Assert.That(tracker.IsBuildingBlock, Is.False);
        }
    }

    [Test]
    public async Task Producer_environments_track_their_builds([Values] bool transient)
    {
        await using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        IBlockProducerEnvFactory factory = container.Resolve<IBlockProducerEnvFactory>();

        if (transient)
        {
            await using ScopedBlockProducerEnv env = factory.CreateTransient();
            Assert.That(env.ChainProcessor, Is.InstanceOf<BlockBuildingTrackingProcessor>());
        }
        else
        {
            Assert.That(factory.CreatePersistent().ChainProcessor, Is.InstanceOf<BlockBuildingTrackingProcessor>());
        }

        Assert.That(container.Resolve<IChainHeadInfoProvider>().IsBuildingBlock, Is.False);
    }
}
