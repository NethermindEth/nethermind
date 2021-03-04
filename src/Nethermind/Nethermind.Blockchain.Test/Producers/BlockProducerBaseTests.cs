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
using System.Collections.Generic;
using System.Security;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    [TestFixture]
    public class BlockProducerBaseTests
    {
        private class ProducerUnderTest : BlockProducerBase
        {
            public ProducerUnderTest(ITxSource txSource, IBlockchainProcessor processor, ISealer sealer, IBlockTree blockTree, IBlockProcessingQueue blockProcessingQueue, IStateProvider stateProvider, IGasLimitCalculator gasLimitCalculator, ITimestamper timestamper, IBlockPreparationContextService blockPreparationContextService, ILogManager logManager)
                : base(txSource, processor, sealer, blockTree, blockProcessingQueue, stateProvider, gasLimitCalculator, timestamper, MainnetSpecProvider.Instance, blockPreparationContextService, logManager)
            {
            }

            public override void Start() { }

            public override Task StopAsync() => Task.CompletedTask;

            protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp)
            {
                return timestamp;
            }

            public Block Prepare()
            {
                return PrepareBlock(Build.A.BlockHeader.TestObject);
            }
            
            public Block Prepare(BlockHeader header)
            {
                return PrepareBlock(header);
            }

            protected override bool IsRunning() => true;
        }
        
        [Test]
        public void Time_passing_does_not_break_the_block()
        {
            ITimestamper timestamper = new IncrementalTimestamper();
            ProducerUnderTest producerUnderTest = new ProducerUnderTest(
                EmptyTxSource.Instance, 
                Substitute.For<IBlockchainProcessor>(),
                NullSealEngine.Instance,
                Build.A.BlockTree().TestObject,
                Substitute.For<IBlockProcessingQueue>(),
                Substitute.For<IStateProvider>(),
                Substitute.For<IGasLimitCalculator>(),
                timestamper,
                new BlockPreparationContextService(),
                LimboLogs.Instance);

            Block block = producerUnderTest.Prepare();
            block.Timestamp.Should().BeEquivalentTo(block.Difficulty);
        }
        
        [Test]
        public void Parent_timestamp_is_used_consistently()
        {
            ITimestamper timestamper = new IncrementalTimestamper(DateTime.UnixEpoch, TimeSpan.FromSeconds(1));
            ProducerUnderTest producerUnderTest = new ProducerUnderTest(
                EmptyTxSource.Instance, 
                Substitute.For<IBlockchainProcessor>(),
                NullSealEngine.Instance,
                Build.A.BlockTree().TestObject,
                Substitute.For<IBlockProcessingQueue>(),
                Substitute.For<IStateProvider>(),
                Substitute.For<IGasLimitCalculator>(),
                timestamper,
                new BlockPreparationContextService(),
                LimboLogs.Instance);

            ulong futureTime = UnixTime.FromSeconds(TimeSpan.FromDays(1).TotalSeconds).Seconds;
            Block block = producerUnderTest.Prepare(Build.A.BlockHeader.WithTimestamp(futureTime).TestObject);
            block.Timestamp.Should().BeEquivalentTo(block.Difficulty);
        }

        public static class BaseFeeTestScenario
        {
            public class ScenarioBuilder
            {
                private long _eip1559TransitionBlock;
                private bool _eip1559Enabled;
                private TestRpcBlockchain _testRpcBlockchain;
                private Task<ScenarioBuilder> antecedent;
                
                public ScenarioBuilder WithEip1559TransitionBlock(long transitionBlock)
                {
                    _eip1559Enabled = true;
                    _eip1559TransitionBlock = transitionBlock;
                    return this;
                }

                private async Task<ScenarioBuilder> CreateTestBlockchainAsync()
                {
                    await ExecuteAntecedentIfNeeded();
                    Address address = TestItem.Addresses[0];
                    SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(
                        new ReleaseSpec()
                        {
                            IsEip1559Enabled = _eip1559Enabled, Eip1559TransitionBlock = _eip1559TransitionBlock
                        }, 1);
                    BlockBuilder blockBuilder = Core.Test.Builders.Build.A.Block.Genesis.WithGasLimit(10000000000);
                    _testRpcBlockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                        .WithGenesisBlockBuilder(blockBuilder)
                        .Build(spec);
                    _testRpcBlockchain.TestWallet.UnlockAccount(address, new SecureString());
                    await _testRpcBlockchain.AddFunds(address, 1.Ether());
                    return this;
                }
                
                public ScenarioBuilder CreateTestBlockchain()
                {
                    antecedent = CreateTestBlockchainAsync();
                    return this;
                }
                
                public ScenarioBuilder BlocksBeforeTransitionShouldHaveZeroBaseFee()
                {
                    antecedent = BlocksBeforeTransitionShouldHaveZeroBaseFeeAsync();
                    return this;
                }
                
                private async Task<ScenarioBuilder> BlocksBeforeTransitionShouldHaveZeroBaseFeeAsync()
                {
                    await ExecuteAntecedentIfNeeded();
                    IBlockTree blockTree = _testRpcBlockchain.BlockTree;
                    Block? startingBlock = blockTree.Head;
                    Assert.AreEqual(UInt256.Zero, startingBlock.Header.BaseFee);
                    for (long i = startingBlock.Number; i < _eip1559TransitionBlock - 1; ++i)
                    {
                        await _testRpcBlockchain.AddBlock();
                        Block? currentBlock = blockTree.Head;
                        Assert.AreEqual(UInt256.Zero, currentBlock.Header.BaseFee);
                    }

                    return this;
                }

                private async Task ExecuteAntecedentIfNeeded()
                {
                    if (antecedent != null)
                        await antecedent;
                }

                public async Task Finish()
                {
                    await ExecuteAntecedentIfNeeded();
                }
            }
            
            public static ScenarioBuilder GoesLikeThis()
            {
                return new ScenarioBuilder();
            }
        }

        [Test]
        public async Task BlockProducerShouldCalculateBaseFee()
        {
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(5)
                .CreateTestBlockchain()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee();
            await scenario.Finish();
        }
    }
}
