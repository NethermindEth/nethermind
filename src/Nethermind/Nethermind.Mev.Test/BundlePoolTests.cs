// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

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
                yield return new BundleTest(4, DefaultTimestamp, 1, 0);
                yield return new BundleTest(8, DefaultTimestamp, 0, 0);
                yield return new BundleTest(9, DefaultTimestamp, 3, 0);
                yield return new BundleTest(10, 8, 0, 1, // only latest bundle from same relay
                    p => p.AddBundle(new MevBundle(10, Array.Empty<BundleTransaction>(), 5, 7)));
                yield return new BundleTest(11, DefaultTimestamp, 0, 0);
                yield return new BundleTest(12, DefaultTimestamp, 1, 0);
                yield return new BundleTest(15, DefaultTimestamp, 1, 0);
            }
        }

        [TestCaseSource(nameof(BundleRetrievalTest))]
        public void should_retrieve_right_bundles_from_pool(BundleTest test)
        {
            TestContext testContext = new();
            test.Action?.Invoke(testContext.BundlePool);
            List<MevBundle> bundles = testContext.BundlePool.GetBundles(test.Block, test.TestTimestamp).ToList();
            bundles.Count.Should().Be(test.ExpectedBundleCount);
            List<MevBundle> megabundles =
                testContext.BundlePool.GetMegabundles(test.Block, test.TestTimestamp).ToList();
            megabundles.Count.Should().Be(test.ExpectedMegabundleCount);
        }

        [TestCaseSource(nameof(BundleRetrievalTest))]
        public void should_retire_bundles_from_pool_on_block_tree_progression(BundleTest test)
        {
            TestContext testContext = new();
            test.Action?.Invoke(testContext.BundlePool);

            testContext.BlockTree.NewHeadBlock +=
                Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(test.Block - 1).TestObject));

            List<MevBundle> bundlesForFutureBlock =
                testContext.BundlePool.GetBundles(test.Block, test.TestTimestamp).ToList();
            bundlesForFutureBlock.Count.Should().Be(test.ExpectedBundleCount);
            List<MevBundle> megabundlesForFutureBlock =
                testContext.BundlePool.GetMegabundles(test.Block, test.TestTimestamp).ToList();
            megabundlesForFutureBlock.Count.Should().Be(test.ExpectedMegabundleCount);

            testContext.BlockTree.NewHeadBlock +=
                Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(test.Block).TestObject));

            List<MevBundle> bundlesForPastBlock =
                testContext.BundlePool.GetBundles(test.Block, test.TestTimestamp).ToList();
            bundlesForPastBlock.Count.Should().Be(0);
            List<MevBundle> megabundlesForPastBlock =
                testContext.BundlePool.GetMegabundles(test.Block, test.TestTimestamp).ToList();
            megabundlesForPastBlock.Count.Should().Be(0);
        }

        [Test]
        public void should_add_bundle_with_correct_timestamps()
        {
            ITimestamper timestamper = new ManualTimestamper(DateTime.UtcNow);
            ulong timestamp = timestamper.UnixTime.Seconds;

            TestContext test = new TestContext(timestamper);

            BundleTransaction[] tx1 =
            {
                Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyD).TestObject
            };
            BundleTransaction[] tx2 =
            {
                Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject
            };

            MevConfig mevConfig = new MevConfig();
            UInt256 bundleHorizon = mevConfig.BundleHorizon;
            MevBundle[] bundles = new[]
            {
                new MevBundle(1, tx1, 0, 0), new MevBundle(2, tx1, timestamp + 5, timestamp), //min > max
                new MevBundle(3, tx1, timestamp - 5, timestamp + 5),
                new MevBundle(4, tx2, timestamp - 10, timestamp - 5), //max < curr
                new MevBundle(4, tx2, timestamp + bundleHorizon, timestamp + bundleHorizon + 10),
                new MevBundle(5, tx2, timestamp + bundleHorizon + 1, timestamp + bundleHorizon + 10) //min too large
            };

            bundles.Select(b => test.BundlePool.AddBundle(b))
                .Should().BeEquivalentTo(new bool[] { true, false, true, false, true, false });
        }

        [Test]
        public void should_reject_bundle_without_transactions()
        {
            TestContext tc = new();

            //Adding empty transaction array bundles
            MevBundle bundleNoTx1 = new MevBundle(10, Array.Empty<BundleTransaction>());
            tc.BundlePool.AddBundle(bundleNoTx1).Should().BeFalse();
        }

        [Test]
        public void should_reject_megabundle_without_transactions()
        {
            TestContext tc = new();
            MevMegabundle bundleNoTx = new(10, Array.Empty<BundleTransaction>());
            tc.BundlePool.AddBundle(bundleNoTx).Should().BeFalse();
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

        [TestCase(10, ExpectedResult = false)]
        [TestCase(15, ExpectedResult = false)]
        [TestCase(16, ExpectedResult = false)]
        [TestCase(17, ExpectedResult = true)]
        public bool should_reject_megabundle_on_block_before_head(long bundleBlock)
        {
            BundleTransaction[] filledArr =
            {
                Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyD).TestObject,
                Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject
            };

            TestContext tc = new TestContext();
            Block block = Build.A.Block.WithNumber(16).TestObject;
            tc.BlockTree.Head.Returns(block);

            MevMegabundle bundle = new(bundleBlock, filledArr);
            return tc.BundlePool.AddMegabundle(bundle);
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
            var ecdsa = Substitute.For<IEthereumEcdsa>();
            ecdsa.RecoverAddress(Arg.Any<Signature>(), Arg.Any<Keccak>()).Returns(
                TestItem.AddressA,
                TestItem.AddressB,
                TestItem.AddressC
            );
            TestContext testContext =
                new(
                    config: new MevConfig()
                    {
                        TrustedRelays = $"{TestItem.AddressA},{TestItem.AddressB},{TestItem.AddressC}"
                    }, ecdsa: ecdsa);

            async Task CheckSimulationsForBlock(int head, int numberOfSimulationsToReceive)
            {
                testContext.BlockTree.NewHeadBlock +=
                    Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(head).TestObject)); //4
                for (int i = 0; i < numberOfSimulationsToReceive; i++)
                {
                    await ss.WaitAsync(TimeSpan.FromMilliseconds(10));
                }

                await testContext.Simulator.Received(numberOfSimulationsToReceive).Simulate(Arg.Any<MevBundle>(),
                    Arg.Any<BlockHeader>(),
                    Arg.Any<CancellationToken>());
                testContext.Simulator.ClearReceivedCalls();
            }

            testContext.Simulator.Simulate(Arg.Any<MevBundle>(), Arg.Any<BlockHeader>())
                .Returns(SimulatedMevBundle.Cancelled(new MevBundle(1, Array.Empty<BundleTransaction>())))
                .AndDoes(c => ss.Release());

            int head = 4;

            await CheckSimulationsForBlock(head++, 1); //4 -> 5
            await CheckSimulationsForBlock(head++, 1); //5 -> 6
            await CheckSimulationsForBlock(head++, 0); //6 -> 7
            await CheckSimulationsForBlock(head++, 0); //7 -> 8
            await CheckSimulationsForBlock(head++, 4); //8 -> 9
            await CheckSimulationsForBlock(head, 2);
        }

        [Test]
        public static void should_simulate_bundle_on_added_when_in_next_block()
        {
            TestContext test = new();
            Block block = Build.A.Block.WithNumber(5).TestObject;
            test.BlockTree.Head.Returns(block);
            MevBundle mevBundle = new MevBundle(6,
                new[]
                {
                    Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject
                });
            test.BundlePool.AddBundle(mevBundle);

            test.Simulator.Received(1).Simulate(mevBundle, block.Header, Arg.Any<CancellationToken>());
        }

        [Test]
        public static void should_simulate_megabundle_on_added_when_in_next_block()
        {
            TestContext test = new();
            Block block = Build.A.Block.WithNumber(5).TestObject;
            test.BlockTree.Head.Returns(block);
            MevMegabundle bundle = new(6,
                new[]
                {
                    Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject
                });
            test.BundlePool.AddMegabundle(bundle);

            test.Simulator.Received(1).Simulate(bundle, block.Header, Arg.Any<CancellationToken>());
        }

        [TestCase(0, new int[] { 1, 0, 0, 0, 0, 0 })]
        [TestCase(1, new int[] { 0, 1, 0, 0, 0, 0 })]
        [TestCase(2, new int[] { 0, 0, 1, 0, 0, 0 })]
        [TestCase(3, new int[] { 0, 0, 0, 0, 0, 0 })]
        [TestCase(4, new int[] { 0, 0, 0, 0, 0, 0 })]
        [TestCase(5, new int[] { 0, 0, 0, 0, 0, 3 })]
        public async Task should_remove_bundle_simulations_on_eviction(long headDelta, int[] expectedSimulations)
        {
            int head = 3;
            IBundleSimulator bundleSimulator = Substitute.For<IBundleSimulator>();
            SimulatedMevBundle successfulBundle = new(new MevBundle(1, Array.Empty<BundleTransaction>()),
                0, true, UInt256.Zero, UInt256.Zero, UInt256.Zero);

            bundleSimulator.Simulate(Arg.Any<MevBundle>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(successfulBundle));

            TestContext tc = new(default, bundleSimulator, new MevConfig() { BundlePoolSize = 10 }, default, head);
            ISimulatedBundleSource simulatedBundleSource = tc.BundlePool;

            async Task<IEnumerable<SimulatedMevBundle>> GetSimulatedBundlesForBlock(int blockNumber)
            {
                return await simulatedBundleSource
                    .GetBundles(Build.A.BlockHeader.WithNumber(blockNumber).TestObject,
                        tc.Timestamper.UnixTime.Seconds,
                        long.MaxValue);
            }

            long buildBlockNumber = head + headDelta;

            async Task CheckSimulatedBundles(int[] expectedSimulationsLength)
            {
                CancellationTokenSource cts = new(TestBlockchain.DefaultTimeout);
                foreach (MevBundle bundle in tc.Bundles.Where(b => b.BlockNumber == buildBlockNumber + 1 && b is not MevMegabundle))
                {
                    await tc.BundlePool.WaitForSimulationToFinish(bundle, cts.Token);
                }

                for (int i = 0; i < expectedSimulationsLength.Length; i++)
                {
                    SimulatedMevBundle[] simulatedBundles = (await GetSimulatedBundlesForBlock(i + head)).ToArray();
                    simulatedBundles.Length.Should().Be(expectedSimulationsLength[i]);
                }
            }

            tc.BlockTree.NewHeadBlock +=
                Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(buildBlockNumber).TestObject));
            await CheckSimulatedBundles(expectedSimulations);
        }

        [TestCase(0, new int[] { 0, 0, 0 })]
        [TestCase(1, new int[] { 0, 1, 0 })]
        [TestCase(2, new int[] { 0, 0, 2 })]
        public async Task should_remove_megabundle_simulations_on_eviction(long headDelta, int[] expectedSimulations)
        {
            int head = 7;
            IBundleSimulator bundleSimulator = Substitute.For<IBundleSimulator>();
            SimulatedMevBundle successfulBundle = new(new MevBundle(1, Array.Empty<BundleTransaction>()),
                0, true, UInt256.Zero, UInt256.Zero, UInt256.Zero);

            bundleSimulator.Simulate(Arg.Any<MevBundle>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(successfulBundle));

            var ecdsa = Substitute.For<IEthereumEcdsa>();
            ecdsa.RecoverAddress(Arg.Any<Signature>(), Arg.Any<Keccak>()).Returns(
                TestItem.AddressA,
                TestItem.AddressB,
                TestItem.AddressC
            );

            TestContext tc = new(default, bundleSimulator,
                new MevConfig()
                {
                    BundlePoolSize = 10,
                    TrustedRelays = $"{TestItem.AddressA},{TestItem.AddressB},{TestItem.AddressC}"
                }, ecdsa, head);
            ISimulatedBundleSource simulatedBundleSource = tc.BundlePool;

            async Task<IEnumerable<SimulatedMevBundle>> GetSimulatedBundlesForBlock(int blockNumber)
            {
                return await simulatedBundleSource
                    .GetMegabundles(Build.A.BlockHeader.WithNumber(blockNumber).TestObject,
                        tc.Timestamper.UnixTime.Seconds,
                        long.MaxValue);
            }

            long buildBlockNumber = head + headDelta;

            async Task CheckSimulatedBundles(int[] expectedSimulationsLength)
            {
                CancellationTokenSource cts = new(TestBlockchain.DefaultTimeout + 100000);
                foreach (MevBundle bundle in tc.Bundles.Where(b => b.BlockNumber == buildBlockNumber + 1))
                {
                    await tc.BundlePool.WaitForSimulationToFinish(bundle, cts.Token);
                }

                for (int i = 0; i < expectedSimulationsLength.Length; i++)
                {
                    SimulatedMevBundle[] simulatedBundles = (await GetSimulatedBundlesForBlock(i + head)).ToArray();
                    simulatedBundles.Length.Should().Be(expectedSimulationsLength[i]);
                }
            }

            tc.BlockTree.NewHeadBlock +=
                Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(buildBlockNumber).TestObject));
            await CheckSimulatedBundles(expectedSimulations);
        }

        [Test]
        [Ignore("ToDo - it is failing after the merge changes")]
        public async Task should_remove_bundle_when_simulation_fails()
        {
            var chain = await MevRpcModuleTests.CreateChain(1);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Address contractAddress =
                await MevRpcModuleTests.Contracts.Deploy(chain, MevRpcModuleTests.Contracts.ReverterCode);
            BundleTransaction revertingBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(MevRpcModuleTests.Contracts.LargeGasLimit)
                .WithGasPrice(500).WithTo(contractAddress)
                .WithData(Bytes.FromHexString(MevRpcModuleTests.Contracts.ReverterInvokeFail))
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            BundleTransaction normalBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            MevBundle failingBundle = new(2, new[] { revertingBundleTx });
            MevBundle normalBundle = new(2, new[] { normalBundleTx });

            chain.BundlePool.AddBundle(failingBundle);
            chain.BundlePool.AddBundle(normalBundle);

            CancellationTokenSource cts = new(TestBlockchain.DefaultTimeout);
            (await chain.BundlePool.WaitForSimulationToFinish(failingBundle, cts.Token)).Should().BeFalse();
            (await chain.BundlePool.WaitForSimulationToFinish(normalBundle, cts.Token)).Should().BeTrue();

            await Task.Delay(10, cts.Token); //give time for background task to remove failed simulation

            MevBundle[] bundlePoolBundles = chain.BundlePool.GetBundles(2, UInt256.Zero).ToArray();
            bundlePoolBundles.Should().BeEquivalentTo(normalBundle);
        }

        [Test]
        public void should_evict_megabundle_when_relay_sends_new_bundle()
        {
            var ecdsa = Substitute.For<IEthereumEcdsa>();
            ecdsa.RecoverAddress(Arg.Any<Signature>(), Arg.Any<Keccak>()).Returns(
                TestItem.AddressA,
                TestItem.AddressB
            );
            TestContext tc = new(config: new MevConfig() { TrustedRelays = $"{TestItem.AddressA},{TestItem.AddressB}" }, ecdsa: ecdsa);
            tc.BundlePool.GetMegabundles(9, DefaultTimestamp).Count().Should().Be(1);
            tc.BundlePool.GetMegabundles(10, DefaultTimestamp).Count().Should().Be(1);
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

        [TestCase(1u, 0)]
        [TestCase(6u, 1)]
        [TestCase(11u, 2)]
        [TestCase(100u, 1)]
        [TestCase(200u, 0)]
        public static void should_retrieve_megabundles_by_timestamp(uint timestamp, int expectedCount)
        {
            var ecdsa = Substitute.For<IEthereumEcdsa>();
            ecdsa.RecoverAddress(Arg.Any<Signature>(), Arg.Any<Keccak>()).Returns(
                TestItem.AddressA,
                TestItem.AddressB,
                TestItem.AddressC
            );
            MevConfig mevConfig = new() { TrustedRelays = $"{TestItem.AddressA},{TestItem.AddressB},{TestItem.AddressC}" };
            TestContext test = new(config: mevConfig, ecdsa: ecdsa);
            test.BundlePool.GetMegabundles(10, timestamp).Should().HaveCount(expectedCount);
        }

        [Test]
        public static void should_evict_bundles_by_block_number_and_min_timestamp()
        {
            MevConfig mevConfig = new() { BundlePoolSize = 5 };
            TestContext test = new(config: mevConfig);

            Dictionary<long, int> expectedCountPerBlock = new()
            {
                { 4, 1 },
                { 5, 1 },
                { 6, 1 },
                { 9, 2 },
                { 12, 0 },
                { 15, 0 }
            };

            foreach (var expectedCount in expectedCountPerBlock)
            {
                test.BundlePool.GetBundles(expectedCount.Key, 10).Should().HaveCount(expectedCount.Value);
            }
        }

        public record BundleTest(long Block, ulong TestTimestamp, int ExpectedBundleCount, int ExpectedMegabundleCount,
            Action<BundlePool>? Action = null);

        private class TestContext
        {
            public TestContext(ITimestamper? timestamper = null, IBundleSimulator? bundleSimulator = null,
                IMevConfig? config = null, IEthereumEcdsa? ecdsa = null, long? blockTreeHead = null,
                bool addTestBundles = true)
            {
                BundleTransaction CreateTransaction(PrivateKey privateKey) => Build.A
                    .TypedTransaction<BundleTransaction>().SignedAndResolved(privateKey).TestObject;

                if (blockTreeHead is not null)
                {
                    BlockTree.Head.Returns(Build.A.Block.WithNumber((long)blockTreeHead).TestObject);
                }

                BlockTree.NetworkId.Returns((ulong)TestBlockchainIds.NetworkId);
                BlockTree.ChainId.Returns((ulong)TestBlockchainIds.ChainId);

                if (timestamper is not null)
                {
                    Timestamper = timestamper;
                }

                if (bundleSimulator is not null)
                {
                    Simulator = bundleSimulator;
                }

                if (ecdsa is not null)
                {
                    EthereumEcdsa = ecdsa;
                }
                else
                {
                    EthereumEcdsa.RecoverAddress(Arg.Any<Signature>(), Arg.Any<Keccak>()).Returns(TestItem.AddressA);
                }

                BundlePool = new TestBundlePool(
                    BlockTree,
                    Simulator,
                    Timestamper,
                    new TxValidator(TestSpecProvider.Instance),
                    new TestSpecProvider(London.Instance),
                    config ?? new MevConfig() { TrustedRelays = TestItem.AddressA.ToString() },
                    LimboLogs.Instance,
                    EthereumEcdsa);

                if (!addTestBundles) return;
                BundleTransaction tx1 = CreateTransaction(TestItem.PrivateKeyA);
                BundleTransaction tx2 = CreateTransaction(TestItem.PrivateKeyB);
                BundleTransaction tx3 = CreateTransaction(TestItem.PrivateKeyC);

                AddBundle(CreateBundle(4, tx1));
                AddBundle(CreateBundle(5, tx2));
                AddBundle(CreateBundle(6, tx2));
                AddBundle(new MevBundle(9, new[] { tx1 }, 0, 0));
                AddBundle(new MevBundle(9, new[] { tx2 }, 10, 100));
                AddBundle(new MevBundle(9, new[] { tx3 }, 5, 50));
                AddMegabundle(CreateMegabundle(9, tx1, tx2, tx3));
                AddMegabundle(new MevMegabundle(10, new[] { tx1 }, minTimestamp: 10, maxTimestamp: 100));
                AddMegabundle(new MevMegabundle(10, new[] { tx1 }, minTimestamp: 5, maxTimestamp: 50));
                AddBundle(CreateBundle(12, tx1, tx2, tx3));
                AddBundle(CreateBundle(15, tx1, tx2));
            }

            private void AddBundle(MevBundle bundle)
            {
                Bundles.Add(bundle);
                BundlePool.AddBundle(bundle);
            }

            private void AddMegabundle(MevMegabundle megabundle)
            {
                Bundles.Add(megabundle);
                BundlePool.AddMegabundle(megabundle);
            }

            public List<MevBundle> Bundles = new();
            public IBundleSimulator Simulator { get; } = Substitute.For<IBundleSimulator>();
            public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
            public TestBundlePool BundlePool { get; }

            public ITimestamper Timestamper { get; private set; } =
                new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(DefaultTimestamp));

            public IEthereumEcdsa EthereumEcdsa { get; } = Substitute.For<IEthereumEcdsa>();

            private MevBundle CreateBundle(long blockNumber, params BundleTransaction[] transactions) =>
                new(blockNumber, transactions);

            private MevMegabundle CreateMegabundle(long blockNumber, params BundleTransaction[] transactions) =>
                new(blockNumber, transactions);
        }
    }
}
