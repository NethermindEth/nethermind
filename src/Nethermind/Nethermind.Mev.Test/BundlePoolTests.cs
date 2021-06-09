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
using Antlr4.Runtime.Misc;
using Avro.File;
using FluentAssertions;
using FluentAssertions.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileSystemGlobbing.Internal.PathSegments;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
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
using Nethermind.Mev.Test;
using Nethermind.TxPool.Collections;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    public class BundlePoolTests
    {
        private const ulong DefaultTimestamp = 10;
        
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
            ITimestamper timestamper = new ManualTimestamper(DateTime.UtcNow);
            ulong timestamp = timestamper.UnixTime.Seconds;
        
            TestContext test = new TestContext(timestamper);

            Transaction[] tx1 = new Transaction[]
            {
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject
            };
            Transaction[] tx2 = new Transaction[]
            {
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject
            };
            
            MevBundle[] bundles = new []
            {
                new MevBundle(1, tx1, 0, 0), //should get added
                new MevBundle(2, tx1, timestamp + 5, timestamp), //should not get added, min > max
                new MevBundle(3, tx1, timestamp - 5,  timestamp + 5), //should get added
                new MevBundle(4, tx1, timestamp + 4000, timestamp + 5000), //should not get added, min time too large
                new MevBundle(4, tx2, timestamp, timestamp + 10), //should get added
                new MevBundle(5, tx2, timestamp + 1, timestamp + 10) //should not get added, min timestamp too large
                
            };

            bundles.Select(b => test.BundlePool.AddBundle(b))
                .Should().BeEquivalentTo(new bool[] {true, false, true, false, true, false});
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
        public void should_reject_bundle_on_block_before_head()
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
            MevBundle bundleFalse3 = new MevBundle(16, filledArr);
            MevBundle bundleTrue1 = new MevBundle(17, filledArr);
            
            tc.BundlePool.AddBundle(bundleFalse1).Should().BeFalse();
            tc.BundlePool.AddBundle(bundleFalse2).Should().BeFalse();
            tc.BundlePool.AddBundle(bundleFalse3).Should().BeFalse();
            tc.BundlePool.AddBundle(bundleTrue1).Should().BeTrue();
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
        public static async Task should_simulate_bundle_when_head_moves()
        {
            SemaphoreSlim ss = new SemaphoreSlim(0);
            TestContext testContext = new TestContext();
            async Task CheckSimulationsForBlock(int head, int numberOfSimulationsToReceive)
            {
                testContext.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(head).TestObject)); //4
                for (int i = 0; i < numberOfSimulationsToReceive; i++)
                {
                    await ss.WaitAsync(TimeSpan.FromMilliseconds(10));
                }
                await testContext.Simulator.Received(numberOfSimulationsToReceive).Simulate(Arg.Any<MevBundle>(), Arg.Any<BlockHeader>(),
                    Arg.Any<CancellationToken>());
                testContext.Simulator.ClearReceivedCalls();
            }
            testContext.Simulator.Simulate(Arg.Any<MevBundle>(), Arg.Any<BlockHeader>()).
                Returns(SimulatedMevBundle.Cancelled(new MevBundle(1, new Transaction[]{}))).AndDoes(c => ss.Release());
            int head = 4;

            await CheckSimulationsForBlock(head++, 1); //4 -> 5
            await CheckSimulationsForBlock(head++, 1); //5 -> 6
            await CheckSimulationsForBlock(head++, 0); //6 -> 7
            await CheckSimulationsForBlock(head++, 0); //7 -> 8
            await CheckSimulationsForBlock(head++, 3); //8 -> 9
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
        public async Task should_remove_simulations_on_eviction()
        {
            SemaphoreSlim ss = new SemaphoreSlim(0);
            int head = 3;
            IBundleSimulator bundleSimulator = Substitute.For<IBundleSimulator>();
            Transaction[] emptyArr = new Transaction[]{};
            bundleSimulator.Simulate(Arg.Any<MevBundle>(), Arg.Any<BlockHeader>())
                .Returns( Task.FromResult(SimulatedMevBundle.Cancelled(new MevBundle(1, emptyArr))))
                .AndDoes(c => ss.Release());
            TestContext tc = new(default, bundleSimulator, new MevConfig() {BundlePoolSize = 4}, head);
            ISimulatedBundleSource simulatedBundleSource = tc.BundlePool;
            
            async Task<IEnumerable<SimulatedMevBundle>> GetSimulatedBundlesForBlock(int blockNumber)
            {
                return await simulatedBundleSource
                        .GetBundles(Build.A.BlockHeader.WithNumber(blockNumber).TestObject,
                        tc.Timestamper.UnixTime.Seconds,
                        long.MaxValue);
            }
            
            async Task CheckSimulatedBundles(int[] expectedSimulations)
            {
                int index = 0;
                for (int i = 3; i <= 8; i++)
                {
                    for (int j = 0; j < expectedSimulations[index]; j++)
                    {
                        await ss.WaitAsync(TimeSpan.FromMilliseconds(10));
                    }
                    SimulatedMevBundle[] simulatedBundles = (await GetSimulatedBundlesForBlock(i)).ToArray();
                    simulatedBundles.Length.Should().Be(expectedSimulations[index++]);
                }
            }
            
            
            await CheckSimulatedBundles(new int[] {1, 0, 0, 0, 0, 0});
            tc.BlockTree.Head.Returns(Build.A.Block.WithNumber(++head).TestObject);
            await CheckSimulatedBundles(new int[] {0, 1, 0, 0, 0, 0});
            tc.BlockTree.Head.Returns(Build.A.Block.WithNumber(++head).TestObject);
            await CheckSimulatedBundles(new int[] {0, 0, 1, 0, 0, 0});
            tc.BlockTree.Head.Returns(Build.A.Block.WithNumber(++head).TestObject);
            await CheckSimulatedBundles(new int[] {0, 0, 0, 0, 0, 0});
            tc.BlockTree.Head.Returns(Build.A.Block.WithNumber(++head).TestObject);
            await CheckSimulatedBundles(new int[] {0, 0, 0, 0, 0, 0});
            tc.BlockTree.Head.Returns(Build.A.Block.WithNumber(++head).TestObject);
            await CheckSimulatedBundles(new int[] {0, 0, 0, 0, 0, 3});
        }
        
        [Test]
        public async Task should_remove_bundle_when_simulation_fails() //not working
        {
            
            var chain = await MevRpcModuleTests.CreateChain(1);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await MevRpcModuleTests.Contracts.Deploy(chain, MevRpcModuleTests.Contracts.ReverterCode);
            Transaction revertingBundleTx = Build.A.Transaction.WithGasLimit(MevRpcModuleTests.Contracts.LargeGasLimit).WithGasPrice(500).WithTo(contractAddress).WithData(Bytes.FromHexString(MevRpcModuleTests.Contracts.ReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            Transaction normalBundleTx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            MevBundle failingBundle = new(2, new[]{revertingBundleTx});
            MevBundle normalBundle = new(2, new[]{normalBundleTx});

            chain.BundlePool.AddBundle(failingBundle);
            chain.BundlePool.AddBundle(normalBundle);

            MevBundle[] bundlePoolBundles = chain.BundlePool.GetBundles(2, UInt256.Zero).ToArray();
            bundlePoolBundles.Should().NotContain(failingBundle);
            bundlePoolBundles.Should().Contain(normalBundle);
            bundlePoolBundles.Length.Should().Be(1);
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
            public TestContext(ITimestamper? timestamper = null, IBundleSimulator? bundleSimulator = null, IMevConfig? config = null, long? BlockTreeHead = null, bool addTestBundles = true)
            {
                Transaction CreateTransaction(PrivateKey privateKey) => Build.A.Transaction.SignedAndResolved(privateKey).TestObject;
                
                if (BlockTreeHead != null)
                {
                    BlockTree.Head.Returns(Build.A.Block.WithNumber((long) BlockTreeHead).TestObject);
                }

                if (timestamper != null)
                {
                    Timestamper = timestamper;
                }

                if (bundleSimulator != null)
                {
                    Simulator = bundleSimulator;
                }
                
                BundlePool = new(
                    BlockTree,
                    Simulator,
                    Timestamper,
                    config ?? new MevConfig(),
                    LimboLogs.Instance);

                if (!addTestBundles) return;
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

            public ITimestamper Timestamper { get; private set; } =
                 new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(DefaultTimestamp)); 
            
            private MevBundle CreateBundle(long blockNumber, params Transaction[] transactions) => new(blockNumber, transactions);
        }
    }
}
