//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using NUnit.Framework.Internal.Execution;

namespace Nethermind.Blockchain.Test.Producers
{
    [TestFixture]
    public partial class BlockProducerBaseTests
    {
        private class ProducerUnderTest : BlockProducerBase
        {
            public ProducerUnderTest(
                ITxSource txSource,
                IBlockchainProcessor processor,
                ISealer sealer,
                IBlockTree blockTree,
                IBlockProductionTrigger blockProductionTrigger,
                IStateProvider stateProvider,
                IGasLimitCalculator gasLimitCalculator,
                ITimestamper timestamper,
                ILogManager logManager,
                IMiningConfig miningConfig)
                : base(
                    txSource,
                    processor,
                    sealer,
                    blockTree,
                    blockProductionTrigger,
                    stateProvider,
                    gasLimitCalculator,
                    timestamper,
                    MainnetSpecProvider.Instance,
                    logManager,
                    new TimestampDifficultyCalculator(),
                    miningConfig)
            {
            }

            public override Task Start() { return Task.CompletedTask; }

            public override Task StopAsync() => Task.CompletedTask;

            public Block Prepare() => PrepareBlock(Build.A.BlockHeader.TestObject);

            public Block Prepare(BlockHeader header) => PrepareBlock(header);

            protected override bool IsRunning() => true;

            private class TimestampDifficultyCalculator : IDifficultyCalculator
            {
                public UInt256 Calculate(BlockHeader header, BlockHeader parent) => header.Timestamp;
            }
        }

        [Test]
        public void Time_passing_does_not_break_the_block()
        {
            ITimestamper timestamper = new IncrementalTimestamper();
            IMiningConfig miningConfig = new MiningConfig();
            ProducerUnderTest producerUnderTest = new(
                EmptyTxSource.Instance,
                Substitute.For<IBlockchainProcessor>(),
                NullSealEngine.Instance,
                Build.A.BlockTree().TestObject,
                Substitute.For<IBlockProductionTrigger>(),
                Substitute.For<IStateProvider>(),
                Substitute.For<IGasLimitCalculator>(),
                timestamper,
                LimboLogs.Instance,
                miningConfig
                );

            Block block = producerUnderTest.Prepare();
            new UInt256(block.Timestamp).Should().BeEquivalentTo(block.Difficulty);
        }

        [Test]
        public void Parent_timestamp_is_used_consistently()
        {
            ITimestamper timestamper = new IncrementalTimestamper(DateTime.UnixEpoch, TimeSpan.FromSeconds(1));
            IMiningConfig miningConfig = new MiningConfig();

            ProducerUnderTest producerUnderTest = new(
                EmptyTxSource.Instance,
                Substitute.For<IBlockchainProcessor>(),
                NullSealEngine.Instance,
                Build.A.BlockTree().TestObject,
                Substitute.For<IBlockProductionTrigger>(),
                Substitute.For<IStateProvider>(),
                Substitute.For<IGasLimitCalculator>(),
                timestamper,
                LimboLogs.Instance,
                miningConfig);

            ulong futureTime = UnixTime.FromSeconds(TimeSpan.FromDays(1).TotalSeconds).Seconds;
            Block block = producerUnderTest.Prepare(Build.A.BlockHeader.WithTimestamp(futureTime).TestObject);
            new UInt256(block.Timestamp).Should().BeEquivalentTo(block.Difficulty);
        }
    }
}
