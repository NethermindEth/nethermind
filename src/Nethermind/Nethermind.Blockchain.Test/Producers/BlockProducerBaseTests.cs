// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

public partial class BlockProducerBaseTests
{
    private class ProducerUnderTest(
        ITxSource txSource,
        IBlockchainProcessor processor,
        ISealer sealer,
        IBlockTree blockTree,
        IWorldState stateProvider,
        IGasLimitCalculator gasLimitCalculator,
        ITimestamper timestamper,
        ILogManager logManager,
        IBlocksConfig blocksConfig)
        : BlockProducerBase(txSource,
            processor,
            sealer,
            blockTree,
            stateProvider,
            gasLimitCalculator,
            timestamper,
            MainnetSpecProvider.Instance,
            logManager,
            new TimestampDifficultyCalculator(),
            blocksConfig)
    {
        public Block Prepare() => PrepareBlock(Build.A.BlockHeader.TestObject);

        public Block Prepare(BlockHeader header) => PrepareBlock(header);

        private class TimestampDifficultyCalculator : IDifficultyCalculator
        {
            public UInt256 Calculate(BlockHeader header, BlockHeader parent) => header.Timestamp;
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Time_passing_does_not_break_the_block()
    {
        ITimestamper timestamper = new IncrementalTimestamper();
        IBlocksConfig blocksConfig = new BlocksConfig();
        ProducerUnderTest producerUnderTest = new(
            EmptyTxSource.Instance,
            Substitute.For<IBlockchainProcessor>(),
            NullSealEngine.Instance,
            Build.A.BlockTree().TestObject,
            Substitute.For<IWorldState>(),
            Substitute.For<IGasLimitCalculator>(),
            timestamper,
            LimboLogs.Instance,
            blocksConfig
            );

        Block block = producerUnderTest.Prepare();
        Assert.That(new UInt256(block.Timestamp), Is.EqualTo(block.Difficulty));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Parent_timestamp_is_used_consistently()
    {
        ITimestamper timestamper = new IncrementalTimestamper(DateTime.UnixEpoch, TimeSpan.FromSeconds(1));
        IBlocksConfig blocksConfig = new BlocksConfig();

        ProducerUnderTest producerUnderTest = new(
            EmptyTxSource.Instance,
            Substitute.For<IBlockchainProcessor>(),
            NullSealEngine.Instance,
            Build.A.BlockTree().TestObject,
            Substitute.For<IWorldState>(),
            Substitute.For<IGasLimitCalculator>(),
            timestamper,
            LimboLogs.Instance,
            blocksConfig);

        ulong futureTime = UnixTime.FromSeconds(TimeSpan.FromDays(1).TotalSeconds).Seconds;
        Block block = producerUnderTest.Prepare(Build.A.BlockHeader.WithTimestamp(futureTime).TestObject);
        Assert.That(new UInt256(block.Timestamp), Is.EqualTo(block.Difficulty));
    }

    [TestCase(false, TestName = "The building flag is raised while the built block executes and cleared after")]
    [TestCase(true, TestName = "The building flag is cleared when executing the built block throws")]
    public async Task Building_flag_brackets_the_built_block_execution(bool processorThrows)
    {
        IBlockTree blockTree = Build.A.BlockTree().TestObject;
        IWorldState state = Substitute.For<IWorldState>();
        state.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        bool flagDuringExecution = false;
        IBlockchainProcessor processor = Substitute.For<IBlockchainProcessor>();
        processor.Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                flagDuringExecution = blockTree.IsBuildingBlock;
                return processorThrows ? throw new InvalidOperationException("execution failed") : call.Arg<Block>();
            });
        ProducerUnderTest producer = new(EmptyTxSource.Instance, processor, NullSealEngine.Instance, blockTree, state,
            Substitute.For<IGasLimitCalculator>(), new IncrementalTimestamper(), LimboLogs.Instance, new BlocksConfig());

        Task<Block?> build = producer.BuildBlock(Build.A.BlockHeader.TestObject, flags: IBlockProducer.Flags.DontSeal);
        if (processorThrows) Assert.ThrowsAsync<InvalidOperationException>(async () => await build);
        else await build;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flagDuringExecution, Is.True, "gossiped validation cannot yield to a build it does not see");
            Assert.That(blockTree.IsBuildingBlock, Is.False, "a flag left raised would defer gossip until the next build");
        }
    }

    [Test]
    public void Building_flag_stays_raised_until_the_last_overlapping_build_ends()
    {
        IBlockTree blockTree = Build.A.BlockTree().TestObject;

        IDisposable first = blockTree.BeginBlockBuilding();
        IDisposable second = blockTree.BeginBlockBuilding();
        first.Dispose();
        first.Dispose();
        bool raisedWhileOneBuildRuns = blockTree.IsBuildingBlock;
        second.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raisedWhileOneBuildRuns, Is.True, "the first build to finish, even disposed twice, must not clear the other's flag");
            Assert.That(blockTree.IsBuildingBlock, Is.False);
        }
    }
}
