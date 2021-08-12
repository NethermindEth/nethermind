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
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Common;
using Microsoft.AspNetCore.Mvc;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
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
using Nethermind.Specs;
using Nethermind.Specs.Forks;
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
                    p => p.AddBundle(new MevBundle(10, Array.Empty<BundleTransaction>(), 5, 7)));
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

            BundleTransaction[] tx1 = {Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyD).TestObject};
            BundleTransaction[] tx2 = {Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject};
            
            MevConfig mevConfig = new MevConfig();
            UInt256 bundleHorizon = mevConfig.BundleHorizon;
            MevBundle[] bundles = new []
            {
                new MevBundle(1, tx1, 0, 0),
                new MevBundle(2, tx1, timestamp + 5, timestamp), //min > max
                new MevBundle(3, tx1, timestamp - 5,  timestamp + 5),
                new MevBundle(4, tx2, timestamp - 10, timestamp - 5), //max < curr
                new MevBundle(4, tx2, timestamp + bundleHorizon , timestamp + bundleHorizon + 10),
                new MevBundle(5, tx2, timestamp + bundleHorizon + 1, timestamp + bundleHorizon + 10) //min too large
                
            };

            bundles.Select(b => test.BundlePool.AddBundle(b))
                .Should().BeEquivalentTo(new bool[] {true, false, true, false, true, false});
        }

        [Test]
        public void should_reject_bundle_without_transactions()
        {
           TestContext tc = new();
           
           //Adding empty transaction array bundles
           MevBundle bundleNoTx1 = new MevBundle(10, Array.Empty<BundleTransaction>());
           tc.BundlePool.AddBundle(bundleNoTx1).Should().BeFalse();
        }
        
        [TestCase(10, ExpectedResult = false)]
        [TestCase(15, ExpectedResult = false)]
        [TestCase(16, ExpectedResult = false)]
        [TestCase(17, ExpectedResult = true)]
        public bool should_reject_bundle_on_block_before_head(long bundleBlock)
        { 
            BundleTransaction[] filledArr =
            {
               Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyD).TestObject,
               Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject
            };
            
            TestContext tc = new TestContext();
            Block block = Build.A.Block.WithNumber(16).TestObject;
            tc.BlockTree.Head.Returns(block);

            MevBundle bundle = new MevBundle(bundleBlock, filledArr);
            return tc.BundlePool.AddBundle(bundle);
        }

        [Test]
        public static void should_replace_bundle_with_same_block_and_transactions()
        {
            TestContext test = new();
            MevBundle[] bundles = test.BundlePool.GetBundles(5, 1).ToArray();
            bundles.Should().HaveCount(1);

            MevBundle replacedBundle = bundles[0];

            MevBundle replacingBundle = new(replacedBundle.BlockNumber, replacedBundle.Transactions, 10, 100);
            test.BundlePool.AddBundle(replacingBundle).Should().BeTrue();

            test.BundlePool.GetBundles(5, 15).Should().BeEquivalentTo(replacingBundle);
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
                Returns(SimulatedMevBundle.Cancelled(new MevBundle(1, Array.Empty<BundleTransaction>()))).AndDoes(c => ss.Release());
            int head = 4;

            await CheckSimulationsForBlock(head++, 1); //4 -> 5
            await CheckSimulationsForBlock(head++, 1); //5 -> 6
            await CheckSimulationsForBlock(head++, 0); //6 -> 7
            await CheckSimulationsForBlock(head++, 0); //7 -> 8
            await CheckSimulationsForBlock(head, 3); //8 -> 9
        }
        
        [Test]
        public static void should_simulate_bundle_on_added_when_in_next_block()
        {
            TestContext test = new();
            Block block = Build.A.Block.WithNumber(5).TestObject;
            test.BlockTree.Head.Returns(block);
            MevBundle mevBundle = new MevBundle(6, new[] {Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject});
            test.BundlePool.AddBundle(mevBundle);
            
            test.Simulator.Received(1).Simulate(mevBundle, block.Header, Arg.Any<CancellationToken>());
        }
        
        [TestCase(0, new int[] {1, 0, 0, 0, 0, 0})]
        [TestCase(1, new int[] {0, 1, 0, 0, 0, 0})]
        [TestCase(2, new int[] {0, 0, 1, 0, 0, 0})]
        [TestCase(3,new int[] {0, 0, 0, 0, 0, 0})]
        [TestCase(4,new int[] {0, 0, 0, 0, 0, 0})]
        [TestCase(5, new int[] {0, 0, 0, 0, 0, 3})]
        public async Task should_remove_simulations_on_eviction(long headDelta, int[] expectedSimulations)
        {
            int head = 3;
            IBundleSimulator bundleSimulator = Substitute.For<IBundleSimulator>();
            SimulatedMevBundle successfulBundle = new(new MevBundle(1, Array.Empty<BundleTransaction>()),
                0, true, UInt256.Zero, UInt256.Zero, UInt256.Zero);

            bundleSimulator.Simulate(Arg.Any<MevBundle>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(successfulBundle));

            TestContext tc = new(default, bundleSimulator, new MevConfig() {BundlePoolSize = 10}, head);
            ISimulatedBundleSource simulatedBundleSource = tc.BundlePool;
            
            async Task<IEnumerable<SimulatedMevBundle>> GetSimulatedBundlesForBlock(int blockNumber)
            {
                return await simulatedBundleSource
                        .GetBundles(Build.A.BlockHeader.WithNumber(blockNumber).TestObject,
                        tc.Timestamper.UnixTime.Seconds,
                        long.MaxValue);
            }

            long buildBlockNumber = head + headDelta;

            async Task CheckSimulatedBundles(int[] expectedSimulationsLenght)
            {
                CancellationTokenSource cts = new(TestBlockchain.DefaultTimeout);
                foreach (MevBundle bundle in tc.Bundles.Where(b => b.BlockNumber == buildBlockNumber + 1))
                {
                    await tc.BundlePool.WaitForSimulationToFinish(bundle, cts.Token);
                }
                
                for (int i = 0; i < expectedSimulationsLenght.Length; i++)
                {
                    SimulatedMevBundle[] simulatedBundles = (await GetSimulatedBundlesForBlock(i + head)).ToArray();
                    simulatedBundles.Length.Should().Be(expectedSimulationsLenght[i]);
                }
            }

            tc.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(buildBlockNumber).TestObject));
            await CheckSimulatedBundles(expectedSimulations);
        }
        
        [Test]
        public async Task should_remove_bundle_when_simulation_fails()
        {
            var chain = await MevRpcModuleTests.CreateChain(1);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await MevRpcModuleTests.Contracts.Deploy(chain, MevRpcModuleTests.Contracts.ReverterCode);
            BundleTransaction revertingBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(MevRpcModuleTests.Contracts.LargeGasLimit)
                .WithGasPrice(500).WithTo(contractAddress)
                .WithData(Bytes.FromHexString(MevRpcModuleTests.Contracts.ReverterInvokeFail))
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            
            BundleTransaction normalBundleTx = Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            MevBundle failingBundle = new(2, new[]{revertingBundleTx});
            MevBundle normalBundle = new(2, new[]{normalBundleTx});

            chain.BundlePool.AddBundle(failingBundle);
            chain.BundlePool.AddBundle(normalBundle);

            CancellationTokenSource cts = new(TestBlockchain.DefaultTimeout);
            await chain.BundlePool.WaitForSimulationToFinish(failingBundle, cts.Token);
            await chain.BundlePool.WaitForSimulationToFinish(normalBundle, cts.Token);

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
            public TestContext(ITimestamper? timestamper = null, IBundleSimulator? bundleSimulator = null, IMevConfig? config = null, long? blockTreeHead = null, bool addTestBundles = true)
            {
                BundleTransaction CreateTransaction(PrivateKey privateKey) => Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(privateKey).TestObject;
                if (blockTreeHead != null)
                {
                    BlockTree.Head.Returns(Build.A.Block.WithNumber((long) blockTreeHead).TestObject);
                }

                BlockTree.ChainId.Returns((ulong) ChainId.Mainnet);

                if (timestamper != null)
                {
                    Timestamper = timestamper;
                }

                if (bundleSimulator != null)
                {
                    Simulator = bundleSimulator;
                }
                
                BundlePool = new TestBundlePool(
                    BlockTree,
                    Simulator,
                    Timestamper,
                    new TxValidator(BlockTree.ChainId),
                    new TestSpecProvider(London.Instance),
                    config ?? new MevConfig(),
                    LimboLogs.Instance);

                if (!addTestBundles) return;
                BundleTransaction tx1 = CreateTransaction(TestItem.PrivateKeyA);
                BundleTransaction tx2 = CreateTransaction(TestItem.PrivateKeyB);
                BundleTransaction tx3 = CreateTransaction(TestItem.PrivateKeyC);

                AddBundle(CreateBundle(4, tx1));
                AddBundle(CreateBundle(5, tx2));
                AddBundle(CreateBundle(6, tx2));
                AddBundle(new MevBundle(9, new []{tx1}, 0, 0));
                AddBundle(new MevBundle(9, new []{tx2}, 10, 100));
                AddBundle(new MevBundle(9, new []{tx3}, 5, 50));
                AddBundle(CreateBundle(12, tx1, tx2, tx3));
                AddBundle(CreateBundle(15, tx1, tx2));
            }

            private void AddBundle(MevBundle bundle)
            {
                Bundles.Add(bundle);
                BundlePool.AddBundle(bundle);
            }

            public List<MevBundle> Bundles = new();
            public IBundleSimulator Simulator { get; } = Substitute.For<IBundleSimulator>();
            public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
            public TestBundlePool BundlePool { get; }

            public ITimestamper Timestamper { get; private set; } =
                 new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(DefaultTimestamp)); 
            
            private MevBundle CreateBundle(long blockNumber, params BundleTransaction[] transactions) => new(blockNumber, transactions);
        }
    }
}
