// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Config;

namespace Nethermind.Clique.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class CliqueRpcModuleTests
    {
        [Test]
        public void Sets_clique_block_producer_properly()
        {
            CliqueConfig cliqueConfig = new();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            
            Signer signer = new(BlockchainIds.Ropsten, TestItem.PrivateKeyA, LimboLogs.Instance);
            CliqueBlockProducer producer = new(
                Substitute.For<ITxSource>(),
                Substitute.For<IBlockchainProcessor>(),
                Substitute.For<IStateProvider>(),
                blockTree,
                Substitute.For<ITimestamper>(),
                Substitute.For<ICryptoRandom>(),
                Substitute.For<ISnapshotManager>(),
                new CliqueSealer(signer, cliqueConfig, Substitute.For<ISnapshotManager>(), LimboLogs.Instance),
                new TargetAdjustedGasLimitCalculator(GoerliSpecProvider.Instance, new BlocksConfig()),
                MainnetSpecProvider.Instance,
                cliqueConfig,
                LimboLogs.Instance);

            SnapshotManager snapshotManager = new(CliqueConfig.Default, new MemDb(), Substitute.For<IBlockTree>(), NullEthereumEcdsa.Instance, LimboLogs.Instance);

            CliqueRpcModule bridge = new(producer, snapshotManager, blockTree);
            Assert.DoesNotThrow(() => bridge.CastVote(TestItem.AddressB, true));
            Assert.DoesNotThrow(() => bridge.UncastVote(TestItem.AddressB));
            Assert.DoesNotThrow(() => bridge.CastVote(TestItem.AddressB, false));
            Assert.DoesNotThrow(() => bridge.UncastVote(TestItem.AddressB));
        }

        [Test]
        public void Can_ask_for_block_signer()
        {
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            BlockHeader header = Build.A.BlockHeader.TestObject;
            blockFinder.FindHeader(Arg.Any<Keccak>()).Returns(header);
            snapshotManager.GetBlockSealer(header).Returns(TestItem.AddressA);
            CliqueRpcModule rpcModule = new(Substitute.For<ICliqueBlockProducer>(), snapshotManager, blockFinder);
            rpcModule.clique_getBlockSigner(Keccak.Zero).Result.ResultType.Should().Be(ResultType.Success);
            rpcModule.clique_getBlockSigner(Keccak.Zero).Data.Should().Be(TestItem.AddressA);
        }

        [Test]
        public void Can_ask_for_block_signer_when_block_is_unknown()
        {
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindHeader(Arg.Any<Keccak>()).Returns((BlockHeader)null);
            CliqueRpcModule rpcModule = new(Substitute.For<ICliqueBlockProducer>(), snapshotManager, blockFinder);
            rpcModule.clique_getBlockSigner(Keccak.Zero).Result.ResultType.Should().Be(ResultType.Failure);
        }

        [Test]
        public void Can_ask_for_block_signer_when_hash_is_null()
        {
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            CliqueRpcModule rpcModule = new(Substitute.For<ICliqueBlockProducer>(), snapshotManager, blockFinder);
            rpcModule.clique_getBlockSigner(null).Result.ResultType.Should().Be(ResultType.Failure);
        }
    }
}
