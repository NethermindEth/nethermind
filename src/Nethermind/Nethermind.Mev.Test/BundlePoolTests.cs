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
using System.Threading.Tasks;
using Avro.File;
using FluentAssertions;
using FluentAssertions.Common;
using Microsoft.AspNetCore.Mvc;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa.Transactions;
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
using NSubstitute.ReturnsExtensions;
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
           TestContext tc = new();
           Transaction[] emptyArr = Array.Empty<Transaction>();
           Transaction[] filledArr = new Transaction[]
           {
               Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject,
               Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject
           };
           
           //Adding empty transaction array bundles
           MevBundle bundleNoTx1 = new MevBundle(10, emptyArr);
           MevBundle bundleNoTx2 = new MevBundle(11, emptyArr);
           tc.BundlePool.AddBundle(bundleNoTx1).Should().BeFalse();
           tc.BundlePool.AddBundle(bundleNoTx2).Should().BeFalse();
           
           //Same bundles with filled transaction array
           MevBundle updatedBundle1 = new MevBundle(10, filledArr);
           MevBundle updatedBundle2 = new MevBundle(11, filledArr);
           tc.BundlePool.AddBundle(updatedBundle1).Should().BeTrue();
           tc.BundlePool.AddBundle(updatedBundle2).Should().BeTrue();
        }
        
        [Test]
        public void should_reject_bundle_on_block_before_head() //Maybe do this with just seeing which ones make it into bundlePool?
        { 
            Transaction[] filledArr = new Transaction[]
            {
               Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject,
               Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject
            };
            
            TestContext tc = new TestContext();
            Block block = Build.A.Block.WithNumber(16).TestObject;
            tc.BlockTree.Head.Returns(block);

            MevBundle bundleFalse1 = new MevBundle(10, filledArr);
            MevBundle bundleFalse2 = new MevBundle(15, filledArr);
            MevBundle bundleTrue1 = new MevBundle(17, filledArr);
            MevBundle bundleTrue2 = new MevBundle(16, filledArr);
            
            tc.BundlePool.AddBundle(bundleFalse1).Should().BeFalse();
            tc.BundlePool.AddBundle(bundleFalse2).Should().BeFalse();
            tc.BundlePool.AddBundle(bundleTrue1).Should().BeTrue();
            tc.BundlePool.AddBundle(bundleTrue2).Should().BeTrue();
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
            // See if simulate gets any returns upon addition of new block to head
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
        public async Task should_remove_simulations_on_eviction() //working on now
        {
            // cast BundlePool to ISimulatedBundleSource and get Bundles
            //Find out which elements are in bundlePool and check to see that the simulations exist beforehand and are removed after
            Transaction[] filledArr = new Transaction[]
            {
               Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject,
               Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject
            };
            MevConfig mevConfig = new MevConfig{BundlePoolSize = 5};
            TestContext tc = new(null, mevConfig); 
            tc.BlockTree.Head.Returns(Build.A.Block.WithNumber(8).TestObject); //not causing bundles to be simulated
            ITimestamper timestamper = new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(1));
            ISimulatedBundleSource simulatedBundleSource = tc.BundlePool; //should we be sorting in descending order by Bundle Number
            
            IEnumerable<SimulatedMevBundle> taskBundles = await simulatedBundleSource!.GetBundles(Build.A.BlockHeader.WithNumber(5).TestObject, timestamper.UnixTime.Seconds, 0,
                    CancellationToken.None); //should get block 5
            SimulatedMevBundle searchFor = new SimulatedMevBundle(
                new MevBundle(9, new[] {Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).TestObject}), 0,
                true,
                UInt256.Zero, UInt256.Zero, UInt256.Zero);
            taskBundles.Should().Contain(searchFor);
            tc.BundlePool.AddBundle(new MevBundle(16, filledArr));
            taskBundles = await simulatedBundleSource!.GetBundles(Build.A.BlockHeader.WithNumber(5).TestObject, timestamper.UnixTime.Seconds, 0,
                                CancellationToken.None);
            taskBundles.Should().NotContain(searchFor);
            //tc.Simulator.Received(3).Returns(4);
        }
        
        [Test]
        public static void should_remove_bundle_when_simulation_fails() //not working
        {
            ITimestamper timestamper = new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(50));
            Transaction[] filledArr = new Transaction[]
            {
               Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject,
               Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject
            };
            
            TestContext tc = new();
            MevBundle newBundle1 = new MevBundle(5, filledArr);
            MevBundle newBundle2 = new MevBundle(6, filledArr);

            long countBlock5Init = tc.BundlePool.GetBundles(5, timestamper.UnixTime.Seconds).Count();
            long countBlock6Init = tc.BundlePool.GetBundles(6, timestamper.UnixTime.Seconds).Count();
            tc.BundlePool.AddBundle(newBundle1);
            tc.BundlePool.AddBundle(newBundle2);
            tc.BundlePool.GetBundles(5, timestamper.UnixTime.Seconds).Count().CompareTo((Int32) countBlock5Init).Should().Be(0);
            tc.BundlePool.GetBundles(6, timestamper.UnixTime.Seconds).Count().CompareTo((Int32) countBlock6Init).Should().Be(0);
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
            public TestContext(ITimestamper? timestamper = null, IMevConfig? config = null, int? BlockTreeHead = null)
            {
                Transaction CreateTransaction(PrivateKey privateKey) => Build.A.Transaction.SignedAndResolved(privateKey).TestObject;
                
                BundlePool = new(
                    BlockTree,
                    Simulator,
                    timestamper ?? new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(1)),
                    config ?? new MevConfig(),
                    LimboLogs.Instance);

                if (BlockTreeHead == null)
                {
                    
                }
                Transaction tx1 = CreateTransaction(TestItem.PrivateKeyA);
                Transaction tx2 = CreateTransaction(TestItem.PrivateKeyB);
                Transaction tx3 = CreateTransaction(TestItem.PrivateKeyC);

                BundlePool.AddBundle(CreateBundle(1, tx1));
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
