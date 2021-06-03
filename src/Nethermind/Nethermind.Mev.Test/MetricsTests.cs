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
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
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
        public void Should_count_valid_bundles_correctly()
        {
            TestBundlePool bundlePool = CreateTestBundlePool();

            int beforeBundlesReceived = Metrics.BundlesReceived;
            int beforeValidBundlesReceived = Metrics.ValidBundlesReceived;
            int beforeBundlesSimulated = Metrics.BundlesSimulated;

            bundlePool.AddBundle(new MevBundle(1, Array.Empty<Transaction>(), 0, 0, default));
            bundlePool.AddBundle(new MevBundle(1, new Transaction[]{Build.A.Transaction.TestObject}, 0, 0, default));
            bundlePool.AddBundle(new MevBundle(3, Array.Empty<Transaction>(), 0, 0, default));
            bundlePool.AddBundle(new MevBundle(4, Array.Empty<Transaction>(), 0, 0, default));
            
            int deltaBundlesReceived = Metrics.BundlesReceived - beforeBundlesReceived;
            int deltaValidBundlesReceived = Metrics.ValidBundlesReceived - beforeValidBundlesReceived;
            int deltaBundlesSimulated = Metrics.BundlesSimulated - beforeBundlesSimulated;

            deltaBundlesReceived.Should().Be(4);
            deltaValidBundlesReceived.Should().Be(4);
            deltaBundlesSimulated.Should().Be(2); // only first two bundles are at current head
        }
        
        [Test]
        public void Should_count_invalid_bundles_correctly()
        {
            TestBundlePool bundlePool = CreateTestBundlePool();

            int beforeBundlesReceived = Metrics.BundlesReceived;
            int beforeValidBundlesReceived = Metrics.ValidBundlesReceived;
            int beforeBundlesSimulated = Metrics.BundlesSimulated;

            bundlePool.AddBundle(new MevBundle(1, Array.Empty<Transaction>(), 5, 0, default)); // invalid
            bundlePool.AddBundle(new MevBundle(2, Array.Empty<Transaction>(), 0, 0, default)); 
            bundlePool.AddBundle(new MevBundle(3, Array.Empty<Transaction>(), 0, long.MaxValue, default)); // invalid
            bundlePool.AddBundle(new MevBundle(4, Array.Empty<Transaction>(), 0, 0, default));
            
            int deltaBundlesReceived = Metrics.BundlesReceived - beforeBundlesReceived;
            int deltaValidBundlesReceived = Metrics.ValidBundlesReceived - beforeValidBundlesReceived;
            int deltaBundlesSimulated = Metrics.BundlesSimulated - beforeBundlesSimulated;

            deltaBundlesReceived.Should().Be(4);
            deltaValidBundlesReceived.Should().Be(2);
            deltaBundlesSimulated.Should().Be(0); // should not simulate invalid bundle 
        }
        
        [Test]
        [Ignore("Coinbase Payments not implemented correctly yet")]
        public async Task Should_count_total_coinbase_payments_correctly()
        {
            var chain = await MevRpcModuleTests.CreateChain(1);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await MevRpcModuleTests.Contracts.Deploy(chain, MevRpcModuleTests.Contracts.CoinbaseCode);
            await MevRpcModuleTests.Contracts.SeedCoinbase(chain, contractAddress);
            Transaction coinbaseTx = Build.A.Transaction.WithGasLimit(MevRpcModuleTests.Contracts.LargeGasLimit).WithData(Bytes.FromHexString(MevRpcModuleTests.Contracts.CoinbaseInvokePay)).WithTo(contractAddress).WithGasPrice(0ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            UInt256 beforeCoinbasePayments = Metrics.TotalCoinbasePayments;

            MevRpcModuleTests.SuccessfullySendBundle(chain, 3, coinbaseTx);
            await chain.AddBlock(true);
            MevRpcModuleTests.GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(MevRpcModuleTests.GetHashes(new []{coinbaseTx}));
            
            UInt256 deltaCoinbasePayments = Metrics.TotalCoinbasePayments - beforeCoinbasePayments;
            
            deltaCoinbasePayments.Should().Be(10000000);
        }

        private static TestBundlePool CreateTestBundlePool()
        {
            var blockTree = Substitute.For<IBlockTree>();
            BlockHeader header = new(
                Keccak.Zero,
                Keccak.Zero,
                Address.Zero,
                UInt256.One,
                0,
                1,
                1,
                null);
            ChainLevelInfo info = new(true, new[] {new BlockInfo(Keccak.Zero, 1)});

            blockTree.FindHeader(Arg.Any<Keccak>(), Arg.Any<BlockTreeLookupOptions>()).Returns(header);
            blockTree.FindLevel(0).ReturnsForAnyArgs(info);

            TestBundlePool bundlePool = new(
                blockTree,
                Substitute.For<IBundleSimulator>(),
                new ManualTimestamper(DateTime.MinValue),
                new MevConfig(),
                LimboLogs.Instance);
            return bundlePool;
        }
    }
}
