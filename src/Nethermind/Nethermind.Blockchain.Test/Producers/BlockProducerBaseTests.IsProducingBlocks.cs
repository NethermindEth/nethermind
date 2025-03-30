// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

[Parallelizable(ParallelScope.All)]
public partial class BlockProducerBaseTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task DevBlockProducer_IsProducingBlocks_returns_expected_results()
    {
        TestRpcBlockchain testRpc = await CreateTestRpc();
        DevBlockProducer blockProducer = new(
            Substitute.For<ITxSource>(),
            testRpc.BlockchainProcessor,
            testRpc.WorldStateManager.GlobalWorldState,
            testRpc.BlockTree,
            testRpc.Timestamper,
            testRpc.SpecProvider,
            new BlocksConfig(),
            LimboLogs.Instance);
        StandardBlockProducerRunner runner = new StandardBlockProducerRunner(
            Substitute.For<IBlockProductionTrigger>(), testRpc.BlockTree, blockProducer);
        await AssertIsProducingBlocks(runner);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task TestBlockProducer_IsProducingBlocks_returns_expected_results()
    {
        TestRpcBlockchain testRpc = await CreateTestRpc();
        IBlocksConfig blocksConfig = new BlocksConfig();
        TestBlockProducer blockProducer = new(
            Substitute.For<ITxSource>(),
            testRpc.BlockchainProcessor,
            testRpc.WorldStateManager.GlobalWorldState,
            Substitute.For<ISealer>(),
            testRpc.BlockTree,
            testRpc.Timestamper,
            testRpc.SpecProvider,
            LimboLogs.Instance,
            blocksConfig);
        StandardBlockProducerRunner runner = new StandardBlockProducerRunner(
            Substitute.For<IBlockProductionTrigger>(), testRpc.BlockTree, blockProducer);
        await AssertIsProducingBlocks(runner);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task MinedBlockProducer_IsProducingBlocks_returns_expected_results()
    {
        TestRpcBlockchain testRpc = await CreateTestRpc();
        IBlocksConfig blocksConfig = new BlocksConfig();
        MinedBlockProducer blockProducer = new(
            Substitute.For<ITxSource>(),
            testRpc.BlockchainProcessor,
            Substitute.For<ISealer>(),
            testRpc.BlockTree,
            testRpc.WorldStateManager.GlobalWorldState,
            Substitute.For<IGasLimitCalculator>(),
            testRpc.Timestamper,
            testRpc.SpecProvider,
            LimboLogs.Instance,
            blocksConfig);
        StandardBlockProducerRunner runner = new StandardBlockProducerRunner(
            Substitute.For<IBlockProductionTrigger>(), testRpc.BlockTree, blockProducer);
        await AssertIsProducingBlocks(runner);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task AuraTestBlockProducer_IsProducingBlocks_returns_expected_results()
    {
        IBlockProcessingQueue blockProcessingQueue = Substitute.For<IBlockProcessingQueue>();
        blockProcessingQueue.IsEmpty.Returns(true);
        AuRaBlockProducer blockProducer = new(
            Substitute.For<ITxSource>(),
            Substitute.For<IBlockchainProcessor>(),
            Substitute.For<IWorldState>(),
            Substitute.For<ISealer>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<ITimestamper>(),
            Substitute.For<IAuRaStepCalculator>(),
            Substitute.For<IReportingValidator>(),
            new AuRaConfig(),
            Substitute.For<IGasLimitCalculator>(),
            Substitute.For<ISpecProvider>(),
            LimboLogs.Instance,
            Substitute.For<IBlocksConfig>());
        StandardBlockProducerRunner runner = new StandardBlockProducerRunner(
            Substitute.For<IBlockProductionTrigger>(), Substitute.For<IBlockTree>(), blockProducer);
        await AssertIsProducingBlocks(runner);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task CliqueBlockProducer_IsProducingBlocks_returns_expected_results()
    {
        TestRpcBlockchain testRpc = await CreateTestRpc();
        CliqueBlockProducer blockProducer = new(
            Substitute.For<ITxSource>(),
            testRpc.BlockchainProcessor,
            testRpc.WorldStateManager.GlobalWorldState,
            testRpc.Timestamper,
            Substitute.For<ICryptoRandom>(),
            Substitute.For<ISnapshotManager>(),
            Substitute.For<ISealer>(),
            Substitute.For<IGasLimitCalculator>(),
            Substitute.For<ISpecProvider>(),
            new CliqueConfig(),
            LimboLogs.Instance);

        CliqueBlockProducerRunner runner = new CliqueBlockProducerRunner(
            testRpc.BlockTree,
            testRpc.Timestamper,
            Substitute.For<ICryptoRandom>(),
            Substitute.For<ISnapshotManager>(),
            blockProducer,
            new CliqueConfig(),
            LimboLogs.Instance);

        await AssertIsProducingBlocks(runner);
    }

    private async Task<TestRpcBlockchain> CreateTestRpc()
    {
        Address address = TestItem.Addresses[0];
        TestSingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance);
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(spec);
        testRpc.TestWallet.UnlockAccount(address, new SecureString());
        await testRpc.AddFunds(address, 1.Ether());
        return testRpc;
    }

    private async Task AssertIsProducingBlocks(IBlockProducerRunner blockProducer)
    {
        Assert.That(blockProducer.IsProducingBlocks(null), Is.EqualTo(false));
        blockProducer.Start();
        Assert.That(blockProducer.IsProducingBlocks(null), Is.EqualTo(true));
        Thread.Sleep(5000);
        Assert.That(blockProducer.IsProducingBlocks(1), Is.EqualTo(false));
        Assert.That(blockProducer.IsProducingBlocks(1000), Is.EqualTo(true));
        Assert.That(blockProducer.IsProducingBlocks(null), Is.EqualTo(true));
        await blockProducer.StopAsync();
        Assert.That(blockProducer.IsProducingBlocks(null), Is.EqualTo(false));
    }
}
