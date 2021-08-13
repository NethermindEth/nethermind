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
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    public class MetricsTests
    {
        [Test]
        public void Are_described()
        {
            Monitoring.Test.MetricsTests.ValidateMetricsDescriptions();
        }

        [Test]
        public void Should_count_valid_bundles()
        {
            TestBundlePool bundlePool = CreateTestBundlePool();

            int beforeBundlesReceived = Metrics.BundlesReceived;
            int beforeValidBundlesReceived = Metrics.ValidBundlesReceived;
            int beforeBundlesSimulated = Metrics.BundlesSimulated;

            bundlePool.AddBundle(new MevBundle(1, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).WithNonce(1).TestObject}, 0, 0));
            bundlePool.AddBundle(new MevBundle(1, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, 0, 0));
            bundlePool.AddBundle(new MevBundle(3, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, 0, 0));
            bundlePool.AddBundle(new MevBundle(4, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, 0, 0));
            
            int deltaBundlesReceived = Metrics.BundlesReceived - beforeBundlesReceived;
            int deltaValidBundlesReceived = Metrics.ValidBundlesReceived - beforeValidBundlesReceived;
            int deltaBundlesSimulated = Metrics.BundlesSimulated - beforeBundlesSimulated;

            deltaBundlesReceived.Should().Be(4);
            deltaValidBundlesReceived.Should().Be(4);
            deltaBundlesSimulated.Should().Be(2); // only first two bundles are at current head
        }
        
        [Test]
        [Explicit("Fails on CI")]
        public void Should_count_invalid_bundles()
        {
            TestBundlePool bundlePool = CreateTestBundlePool();

            int beforeBundlesReceived = Metrics.BundlesReceived;
            int beforeValidBundlesReceived = Metrics.ValidBundlesReceived;
            int beforeBundlesSimulated = Metrics.BundlesSimulated;

            MevBundle[] bundles = new[]
            {
                new MevBundle(1, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, 5, 0), // invalid
                new MevBundle(2, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, 0, 0),
                new MevBundle(3, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, 0, long.MaxValue), // invalid
                new MevBundle(4, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, 0, 0)
            };

            foreach (MevBundle mevBundle in bundles)
            {
                bundlePool.AddBundle(mevBundle);
            }

            int deltaBundlesReceived = Metrics.BundlesReceived - beforeBundlesReceived;
            int deltaValidBundlesReceived = Metrics.ValidBundlesReceived - beforeValidBundlesReceived;
            int deltaBundlesSimulated = Metrics.BundlesSimulated - beforeBundlesSimulated;

            deltaBundlesReceived.Should().Be(4);
            deltaValidBundlesReceived.Should().Be(2);
            deltaBundlesSimulated.Should().Be(0); // should not simulate invalid bundle 
        }
        
        [Test]
        public async Task Should_count_total_coinbase_payments()
        {
            var chain = await MevRpcModuleTests.CreateChain(1);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await MevRpcModuleTests.Contracts.Deploy(chain, MevRpcModuleTests.Contracts.CoinbaseCode);
            BundleTransaction seedContractTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithTo(contractAddress)
                .WithData(Bytes.FromHexString(MevRpcModuleTests.Contracts.CoinbaseDeposit))
                .WithValue(100000000000)
                .WithNonce(1)
                .WithGasLimit(1_000_000)
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            
            await chain.AddBlock(true, seedContractTx);

            //Console.WriteLine((await chain.EthRpcModule.eth_getBalance(contractAddress)).Data!);

            UInt256 beforeCoinbasePayments = (UInt256)Metrics.TotalCoinbasePayments;

            BundleTransaction coinbaseTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(MevRpcModuleTests.Contracts.LargeGasLimit)
                .WithData(Bytes.FromHexString(MevRpcModuleTests.Contracts.CoinbaseInvokePay))
                .WithTo(contractAddress)
                .WithGasPrice(1ul)
                .WithNonce(0)
                .WithValue(0)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            MevRpcModuleTests.SuccessfullySendBundle(chain, 3, coinbaseTx);
            
            await chain.AddBlock(true);
            
            MevRpcModuleTests.GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(MevRpcModuleTests.GetHashes(new []{coinbaseTx}));
            
            UInt256 deltaCoinbasePayments = (UInt256)Metrics.TotalCoinbasePayments - beforeCoinbasePayments;
            deltaCoinbasePayments.Should().Be(100000000000);
        }

        private static TestBundlePool CreateTestBundlePool()
        {
            var blockTree = Substitute.For<IBlockTree>();
            blockTree.ChainId.Returns((ulong)ChainId.Mainnet);
            BlockHeader header = new(
                Keccak.Zero,
                Keccak.Zero,
                Address.Zero,
                UInt256.One,
                0,
                1,
                1,
                null);
            Block head = new Block(header);
            ChainLevelInfo info = new(true, new[] {new BlockInfo(Keccak.Zero, 1)});

            blockTree.Head.Returns(head);
            //blockTree.FindLevel(0).ReturnsForAnyArgs(info);

            TestBundlePool bundlePool = new(
                blockTree,
                Substitute.For<IBundleSimulator>(),
                new ManualTimestamper(DateTimeOffset.UnixEpoch.DateTime),
                new TxValidator(blockTree.ChainId),
                new TestSpecProvider(London.Instance),
                new MevConfig(),
                LimboLogs.Instance);
            return bundlePool;
        }
    }
}
