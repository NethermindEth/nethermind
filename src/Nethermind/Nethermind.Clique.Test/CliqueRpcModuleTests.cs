// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Evm.State;
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
            Signer signer = new(BlockchainIds.Sepolia, TestItem.PrivateKeyA, LimboLogs.Instance);
            CliqueBlockProducer producer = new(
                Substitute.For<ITxSource>(),
                Substitute.For<IBlockchainProcessor>(),
                Substitute.For<IWorldState>(),
                Substitute.For<ITimestamper>(),
                Substitute.For<ICryptoRandom>(),
                Substitute.For<ISnapshotManager>(),
                new CliqueSealer(signer, cliqueConfig, Substitute.For<ISnapshotManager>(), LimboLogs.Instance),
                new TargetAdjustedGasLimitCalculator(HoodiSpecProvider.Instance, new BlocksConfig()),
                MainnetSpecProvider.Instance,
                cliqueConfig,
                LimboLogs.Instance);

            SnapshotManager snapshotManager = new(CliqueConfig.Default, new MemDb(), Substitute.For<IBlockTree>(), NullEthereumEcdsa.Instance, LimboLogs.Instance);

            CliqueBlockProducerRunner producerRunner = new(
                blockTree,
                Substitute.For<ITimestamper>(),
                Substitute.For<ICryptoRandom>(),
                snapshotManager,
                producer,
                cliqueConfig,
                LimboLogs.Instance);

            CliqueRpcModule bridge = new(producerRunner, snapshotManager, blockTree);
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
            blockFinder.FindHeader(Arg.Any<Hash256>()).Returns(header);
            snapshotManager.GetBlockSealer(header).Returns(TestItem.AddressA);
            CliqueRpcModule rpcModule = new(Substitute.For<ICliqueBlockProducerRunner>(), snapshotManager, blockFinder);
            Assert.That(rpcModule.clique_getBlockSigner(Keccak.Zero).Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(rpcModule.clique_getBlockSigner(Keccak.Zero).Data, Is.EqualTo(TestItem.AddressA));
        }

        [Test]
        public void Can_ask_for_block_signer_when_block_is_unknown()
        {
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindHeader(Arg.Any<Hash256>()).Returns((BlockHeader)null);
            CliqueRpcModule rpcModule = new(Substitute.For<ICliqueBlockProducerRunner>(), snapshotManager, blockFinder);
            Assert.That(rpcModule.clique_getBlockSigner(Keccak.Zero).Result.ResultType, Is.EqualTo(ResultType.Failure));
        }

        [Test]
        public void Can_ask_for_block_signer_when_hash_is_null()
        {
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            CliqueRpcModule rpcModule = new(Substitute.For<ICliqueBlockProducerRunner>(), snapshotManager, blockFinder);
            Assert.That(rpcModule.clique_getBlockSigner(null).Result.ResultType, Is.EqualTo(ResultType.Failure));
        }

        [Test]
        public void Registers_clique_rpc_module_via_di_and_resolves_without_block_producer_runner()
        {
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddModule(new CliqueModule())
                .AddSingleton<CliqueChainSpecEngineParameters>(new CliqueChainSpecEngineParameters { Epoch = 30000, Period = 15 })
                // Non-signer node: the runner is not a Clique runner, so the module receives null.
                .AddSingleton<IBlockProducerRunner>(new NoBlockProducerRunner())
                .Build();

            // The module is wired into the RPC provider collection through DI, not an imperative InitRpcModules call.
            Assert.That(
                container.Resolve<IReadOnlyList<RpcModuleInfo>>().Select(static m => m.ModuleType),
                Has.Member(typeof(ICliqueRpcModule)));

            // It resolves and tolerates a null runner: producer-only methods are no-ops rather than throwing.
            ICliqueRpcModule rpcModule = container.Resolve<ICliqueRpcModule>();
            Assert.That(rpcModule.clique_produceBlock(Keccak.Zero).Data, Is.False);
        }
    }
}
