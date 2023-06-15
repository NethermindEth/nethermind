// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.None)] // Metrics are global variables. Dont run with other tests.
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

            bundlePool.AddBundle(new MevBundle(1, new[]{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).WithNonce(1).TestObject}, 0, 0));
            bundlePool.AddBundle(new MevBundle(1, new[] { Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject }, 0, 0));
            bundlePool.AddBundle(new MevBundle(3, new[] { Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject }, 0, 0));
            bundlePool.AddBundle(new MevBundle(4, new[] { Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject }, 0, 0));

            int deltaBundlesReceived = Metrics.BundlesReceived - beforeBundlesReceived;
            int deltaValidBundlesReceived = Metrics.ValidBundlesReceived - beforeValidBundlesReceived;
            int deltaBundlesSimulated = Metrics.BundlesSimulated - beforeBundlesSimulated;

            deltaBundlesReceived.Should().Be(4);
            deltaValidBundlesReceived.Should().Be(4);
            deltaBundlesSimulated.Should().Be(2); // only first two bundles are at current head
        }

        [Test]
        public void Should_count_valid_megabundles()
        {
            var ecdsa = Substitute.For<IEthereumEcdsa>();
            ecdsa.RecoverAddress(Arg.Any<Signature>(), Arg.Any<Keccak>()).Returns(TestItem.AddressB);

            TestBundlePool bundlePool = CreateTestBundlePool(ecdsa);

            int beforeMegabundlesReceived = Metrics.MegabundlesReceived;
            int beforeValidMegabundlesReceived = Metrics.ValidMegabundlesReceived;
            int beforeBundlesSimulated = Metrics.BundlesSimulated;

            bundlePool.AddMegabundle(new MevMegabundle(1, new[] { Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject }));
            bundlePool.AddMegabundle(new MevMegabundle(1, new[] { Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject }));
            bundlePool.AddMegabundle(new MevMegabundle(3, new[] { Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject }));
            bundlePool.AddMegabundle(new MevMegabundle(4, new[] { Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject }));

            int deltaMegabundlesReceived = Metrics.MegabundlesReceived - beforeMegabundlesReceived;
            int deltaValidMegabundlesReceived = Metrics.ValidMegabundlesReceived - beforeValidMegabundlesReceived;
            int deltaMegabundlesSimulated = Metrics.BundlesSimulated - beforeBundlesSimulated;

            deltaMegabundlesReceived.Should().Be(4);
            deltaValidMegabundlesReceived.Should().Be(4);
            deltaMegabundlesSimulated.Should().Be(2); // first two megeabundles are at current head
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
        public void Should_count_invalid_megabundles()
        {
            var ecdsa = Substitute.For<IEthereumEcdsa>();

            MevMegabundle[] bundles = new[]
            {
                new MevMegabundle(1, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, minTimestamp: 5, maxTimestamp: 0), // invalid
                new MevMegabundle(2, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, minTimestamp: 0, maxTimestamp: 0), // invalid relay address
                new MevMegabundle(3, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, minTimestamp: 0, maxTimestamp: long.MaxValue), // invalid
                new MevMegabundle(4, new []{Build.A.TypedTransaction<BundleTransaction>().SignedAndResolved(TestItem.PrivateKeyA).TestObject}, minTimestamp: 0, maxTimestamp: 0)
            };

            ecdsa.RecoverAddress(Arg.Any<Signature>(), bundles[2].Hash).Returns(TestItem.AddressC); // not in list of trusted relay addresses
            ecdsa.RecoverAddress(Arg.Any<Signature>(), bundles[3].Hash).Returns(TestItem.AddressB); // trusted relay address

            TestBundlePool bundlePool = CreateTestBundlePool(ecdsa);

            int beforeMegabundlesReceived = Metrics.MegabundlesReceived;
            int beforeValidMegabundlesReceived = Metrics.ValidMegabundlesReceived;
            int beforeMegabundlesSimulated = Metrics.BundlesSimulated;

            foreach (MevMegabundle mevMegabundle in bundles)
            {
                bundlePool.AddMegabundle(mevMegabundle);
            }

            int deltaBundlesReceived = Metrics.MegabundlesReceived - beforeMegabundlesReceived;
            int deltaValidBundlesReceived = Metrics.ValidMegabundlesReceived - beforeValidMegabundlesReceived;
            int deltaBundlesSimulated = Metrics.BundlesSimulated - beforeMegabundlesSimulated;

            deltaBundlesReceived.Should().Be(4);
            deltaValidBundlesReceived.Should().Be(1);
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

            MevRpcModuleTests.GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(MevRpcModuleTests.GetHashes(new[] { coinbaseTx }));

            UInt256 deltaCoinbasePayments = (UInt256)Metrics.TotalCoinbasePayments - beforeCoinbasePayments;
            deltaCoinbasePayments.Should().Be(100000000000);
        }

        private static TestBundlePool CreateTestBundlePool(IEthereumEcdsa? ecdsa = null, MevConfig? config = null)
        {
            var blockTree = Substitute.For<IBlockTree>();
            blockTree.NetworkId.Returns((ulong)TestBlockchainIds.NetworkId);
            blockTree.ChainId.Returns((ulong)TestBlockchainIds.ChainId);
            BlockHeader header = new(
                Keccak.Zero,
                Keccak.Zero,
                Address.Zero,
                UInt256.One,
                0,
                1,
                1,
                Bytes.Empty);
            Block head = new Block(header);
            ChainLevelInfo info = new(true, new[] { new BlockInfo(Keccak.Zero, 1) });

            blockTree.Head.Returns(head);
            //blockTree.FindLevel(0).ReturnsForAnyArgs(info);

            TestBundlePool bundlePool = new(
                blockTree,
                Substitute.For<IBundleSimulator>(),
                new ManualTimestamper(DateTimeOffset.UnixEpoch.DateTime),
                new TxValidator(blockTree.ChainId),
                new TestSpecProvider(London.Instance),
                config ?? new MevConfig() { TrustedRelays = $"{TestItem.AddressA},{TestItem.AddressB}" },
                LimboLogs.Instance,
                ecdsa ?? Substitute.For<IEthereumEcdsa>());
            return bundlePool;
        }
    }
}
