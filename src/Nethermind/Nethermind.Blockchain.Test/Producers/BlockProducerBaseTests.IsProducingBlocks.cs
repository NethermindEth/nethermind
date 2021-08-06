//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    [Parallelizable(ParallelScope.All)]
    public partial class BlockProducerBaseTests
    {
        [Test]
        public async Task DevBlockProducer_IsProducingBlocks_returns_expected_results()
        {
            TestRpcBlockchain testRpc = await CreateTestRpc();
            DevBlockProducer blockProducer = new DevBlockProducer(
                Substitute.For<ITxSource>(),
                testRpc.BlockchainProcessor,
                testRpc.State,
                testRpc.BlockTree,
                Substitute.For<IBlockProductionTrigger>(),
                testRpc.Timestamper, 
                testRpc.SpecProvider,
                new MiningConfig(),
                LimboLogs.Instance);
            await AssertIsProducingBlocks(blockProducer);
        }

        [Test]
        public async Task TestBlockProducer_IsProducingBlocks_returns_expected_results()
        {
            TestRpcBlockchain testRpc = await CreateTestRpc();
            TestBlockProducer blockProducer = new TestBlockProducer(
                Substitute.For<ITxSource>(),
                testRpc.BlockchainProcessor,
                testRpc.State,
                Substitute.For<ISealer>(),
                testRpc.BlockTree,
                Substitute.For<IBlockProductionTrigger>(),
                testRpc.Timestamper,
                testRpc.SpecProvider,
                LimboLogs.Instance);
            await AssertIsProducingBlocks(blockProducer);
        }
        
        [Test]
        public async Task MinedBlockProducer_IsProducingBlocks_returns_expected_results()
        {
            TestRpcBlockchain testRpc = await CreateTestRpc();
            MinedBlockProducer blockProducer = new MinedBlockProducer(
                Substitute.For<ITxSource>(),
                testRpc.BlockchainProcessor,
                Substitute.For<ISealer>(),
                testRpc.BlockTree,
                Substitute.For<IBlockProductionTrigger>(),
                testRpc.State,
                Substitute.For<IGasLimitCalculator>(),
                testRpc.Timestamper,
                testRpc.SpecProvider,
                LimboLogs.Instance);
            await AssertIsProducingBlocks(blockProducer);
        }
        
        [Test]
        public async Task AuraTestBlockProducer_IsProducingBlocks_returns_expected_results()
        {
            IBlockProcessingQueue blockProcessingQueue = Substitute.For<IBlockProcessingQueue>();
            blockProcessingQueue.IsEmpty.Returns(true);
            AuRaBlockProducer blockProducer = new AuRaBlockProducer(
                Substitute.For<ITxSource>(),
                Substitute.For<IBlockchainProcessor>(),
                Substitute.For<IBlockProductionTrigger>(),
                Substitute.For<IStateProvider>(),
                Substitute.For<ISealer>(),
                Substitute.For<IBlockTree>(),
                Substitute.For<ITimestamper>(),
                Substitute.For<IAuRaStepCalculator>(),
                Substitute.For<IReportingValidator>(),
                new AuRaConfig(),
                Substitute.For<IGasLimitCalculator>(),
                Substitute.For<ISpecProvider>(),
                LimboLogs.Instance);
            await AssertIsProducingBlocks(blockProducer);
        }
        
        [Test]
        public async Task CliqueBlockProducer_IsProducingBlocks_returns_expected_results()
        {
            TestRpcBlockchain testRpc = await CreateTestRpc();
            CliqueBlockProducer blockProducer = new CliqueBlockProducer(
                Substitute.For<ITxSource>(),
                testRpc.BlockchainProcessor,
                testRpc.State,
                testRpc.BlockTree,
                testRpc.Timestamper,
                Substitute.For<ICryptoRandom>(),
                Substitute.For<ISnapshotManager>(),
                Substitute.For<ISealer>(),
                Substitute.For<IGasLimitCalculator>(),
                Substitute.For<ISpecProvider>(),
                new CliqueConfig(),
                LimboLogs.Instance);
            await AssertIsProducingBlocks(blockProducer);
        }

        private async Task<TestRpcBlockchain> CreateTestRpc()
        {
            Address address = TestItem.Addresses[0];
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                .Build(spec);
            testRpc.TestWallet.UnlockAccount(address, new SecureString());
            await testRpc.AddFunds(address, 1.Ether());
            return testRpc;
        }

        private async Task AssertIsProducingBlocks(IBlockProducer blockProducer)
        {
            Assert.AreEqual(false,blockProducer.IsProducingBlocks(null));
            blockProducer.Start();
            Assert.AreEqual(true,blockProducer.IsProducingBlocks(null));
            Thread.Sleep(5000);
            Assert.AreEqual(false,blockProducer.IsProducingBlocks(1));
            Assert.AreEqual(true,blockProducer.IsProducingBlocks(1000));
            Assert.AreEqual(true,blockProducer.IsProducingBlocks(null));
            await blockProducer.StopAsync();
            Assert.AreEqual(false,blockProducer.IsProducingBlocks(null));
        }
    }
}
