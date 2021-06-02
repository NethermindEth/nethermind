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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avro.File;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Runner.Ethereum.Api;
using NSubstitute;
using NSubstitute.Exceptions;
using NUnit.Framework;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    public class BundlePoolTests
    {
        private const ulong DefaultTimestamp = 1_000_000;
        
        public static IEnumerable BundleRetrievalTest
        {
            get
            {
                yield return new BundleTest(4, DefaultTimestamp, 1);
                yield return new BundleTest(8, DefaultTimestamp, 0);
                yield return new BundleTest(9, DefaultTimestamp, 3);
                yield return new BundleTest(10, 8, 0, 
                    p => p.AddBundle(new MevBundle(10, Array.Empty<Transaction>(), 5, 7)));
                yield return new BundleTest(11, DefaultTimestamp, 0);
                yield return new BundleTest(12, DefaultTimestamp, 1);
                yield return new BundleTest(15, DefaultTimestamp, 1);
            }
        }
        
        [TestCaseSource(nameof(BundleRetrievalTest))]
        public void should_retrieve_right_bundles_from_pool(BundleTest test)
        {
            TestContext testContext = new TestContext();
            test.Action?.Invoke(testContext.BundlePool);
            List<MevBundle> result = testContext.BundlePool.GetBundles(test.Block, test.TestTimestamp).ToList();
            result.Count.Should().Be(test.ExpectedCount);
        }
        
        [TestCaseSource(nameof(BundleRetrievalTest))]
        public void should_retire_bundles_from_pool_on_block_tree_progression(BundleTest test)
        {
            TestContext testContext = new TestContext();
            test.Action?.Invoke(testContext.BundlePool);
            
            testContext.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(test.Block - 1).TestObject));
            
            List<MevBundle> bundlesForFutureBlock = testContext.BundlePool.GetBundles(test.Block, test.TestTimestamp).ToList();
            bundlesForFutureBlock.Count.Should().Be(test.ExpectedCount);
            
            testContext.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(test.Block).TestObject));
            
            List<MevBundle> bundlesForPastBlock = testContext.BundlePool.GetBundles(test.Block, test.TestTimestamp).ToList();
            bundlesForPastBlock.Count.Should().Be(0);

        }

        [Test]
        public void should_add_bundle_with_correct_timestamps()
        {
            ITimestamper timestamper = new ManualTimestamper(new DateTime(2021, 1, 1));
            ulong timestamp = timestamper.UnixTime.Seconds;

            TestContext test = new TestContext(timestamper);

            Transaction[] txs = Array.Empty<Transaction>();
            MevBundle[] bundles = new []
            {
                new MevBundle(1, txs, 0, 0), //should get added
                new MevBundle(2, txs, 5, 0), //should not get added, min > max
                new MevBundle(3,  txs, timestamp + 50, timestamp + 100), //should get added
                new MevBundle(4,  txs, timestamp + 4000, timestamp + 5000), //should not get added, min time too large
                
            };

            bundles.Select(b => test.BundlePool.AddBundle(b))
                .Should().BeEquivalentTo(new bool[] {true, false, true, false});
        }

        [Test]
        public void should_reject_bundle_without_transactions()
        {
            
        }
        
        [Test]
        public void should_reject_bundle_on_block_before_head()
        {
            // just set BlockTree.Head to some blockNumber before adding bundle
        }

        [Test]
        public static void should_replace_bundle_with_same_block_and_transactions()
        {
            TestContext test = new();
            MevBundle[] bundles = test.BundlePool.GetBundles(5, 1).ToArray();
            bundles.Should().HaveCount(1);

            MevBundle replacedBundle = bundles[0];

            Keccak[] txHashes = replacedBundle.Transactions.Select(t => t.Hash!).ToArray();
            MevBundle replacingBundle = new(replacedBundle.BlockNumber, replacedBundle.Transactions, 10, 100, txHashes);
            test.BundlePool.AddBundle(replacingBundle).Should().BeTrue();

            test.BundlePool.GetBundles(5, 1).Should().BeEquivalentTo(replacingBundle);
        }
        
        [Test]
        public static void should_simulate_bundle_when_head_moves()
        {
            // 
        }
        
        [Test]
        public static void should_simulate_bundle_on_added_when_in_next_block()
        {
            TestContext test = new();
            Block block = Build.A.Block.WithNumber(5).TestObject;
            test.BlockTree.Head.Returns(block);
            MevBundle mevBundle = new MevBundle(6, new[] {Build.A.Transaction.TestObject});
            test.BundlePool.AddBundle(mevBundle);
            
            test.Simulator.Received(1).Simulate(mevBundle, block.Header, Arg.Any<CancellationToken>());
        }
        
        [Test]
        public static void should_remove_simulations_on_eviction()
        {
            // cast BundlePool to ISimulatedBundleSource and get Bundles
        }
        
        [Test]
        public static void should_remove_bundle_when_simulation_fails()
        {
            
        }
        
        [TestCase(1u, 1)]
        [TestCase(6u, 2)]
        [TestCase(50u, 3)]
        [TestCase(100u, 2)]
        [TestCase(200u, 1)]
        public static void should_retrieve_bundles_by_timestamp(uint timestamp, int expectedCount)
        {
            TestContext test = new();
            test.BundlePool.GetBundles(9, timestamp).Should().HaveCount(expectedCount);
        }

        [Test]
        public static void should_evict_bundles_by_block_number_and_min_timestamp()
        {
            MevConfig mevConfig = new() {BundlePoolSize = 5};
            TestContext test = new(config: mevConfig);
            
            Dictionary<long, int> expectedCountPerBlock = new()
            {
                {4, 1},
                {5, 1},
                {6, 1},
                {9, 2},
                {12, 0},
                {15, 0}
            };

            foreach (var expectedCount in expectedCountPerBlock)
            {
                test.BundlePool.GetBundles(expectedCount.Key, 10).Should().HaveCount(expectedCount.Value);
            }
        }
        
        public record BundleTest(long Block, ulong TestTimestamp, int ExpectedCount, Action<BundlePool>? Action = null);
        
        private class TestContext
        {
            public TestContext(ITimestamper? timestamper = null, IMevConfig? config = null)
            {
                Transaction CreateTransaction(PrivateKey privateKey) => Build.A.Transaction.SignedAndResolved(privateKey).TestObject;

                BundlePool = new(
                    BlockTree,
                    Simulator,
                    timestamper ?? new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(1)),
                    config ?? new MevConfig(),
                    LimboLogs.Instance);

                Transaction tx1 = CreateTransaction(TestItem.PrivateKeyA);
                Transaction tx2 = CreateTransaction(TestItem.PrivateKeyB);
                Transaction tx3 = CreateTransaction(TestItem.PrivateKeyC);

                BundlePool.AddBundle(CreateBundle(4, tx1));
                BundlePool.AddBundle(CreateBundle(5, tx2));
                BundlePool.AddBundle(CreateBundle(6, tx2));
                BundlePool.AddBundle(new MevBundle(9, new []{tx1}, 0, 0));
                BundlePool.AddBundle(new MevBundle(9, new []{tx2}, 10, 100));
                BundlePool.AddBundle(new MevBundle(9, new []{tx3}, 5, 50));
                BundlePool.AddBundle(CreateBundle(12, tx1, tx2, tx3));
                BundlePool.AddBundle(CreateBundle(15, tx1, tx2));
            }

            public IBundleSimulator Simulator { get; } = Substitute.For<IBundleSimulator>();

            public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
            public BundlePool BundlePool { get; }
            
            private MevBundle CreateBundle(long blockNumber, params Transaction[] transactions) => new(blockNumber, transactions);
        }
    }
}
