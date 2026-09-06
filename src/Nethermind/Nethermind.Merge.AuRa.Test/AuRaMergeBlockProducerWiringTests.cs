// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.AuRa.Test;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuRaMergeBlockProducerWiringTests
{
    [Test]
    public void Can_wire_pre_and_post_merge_block_producers_from_container()
    {
        ChainSpec chainSpec = new()
        {
            SealEngineType = SealEngineType.AuRa,
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.WithDifficulty(0).WithAura(0, new byte[65]).WithBlobGasUsed(0).TestObject,
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new AuRaChainSpecEngineParameters
                {
                    WithdrawalContractAddress = TestItem.AddressA,
                    StepDuration = { { 0, 3 } },
                    ValidatorsJson = new AuRaChainSpecEngineParameters.AuRaValidatorJson { List = [Address.Zero] }
                })
        };

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(new MiningConfig { Enabled = true }), chainSpec))
            .AddModule(new AuRaModule(chainSpec))
            .AddModule(new AuRaMergeModule())
            .AddSingleton<NethermindApi.Dependencies>()
            .AddSingleton<IReportingValidator>(NullReportingValidator.Instance)
            .AddSingleton<IBlockProcessingQueue>(Substitute.For<IBlockProcessingQueue>())
            .AddSingleton<IAuRaBlockFinalizationManager>(Substitute.For<IAuRaBlockFinalizationManager>())
            .Build();

        IBlockProducer blockProducer = container.Resolve<IBlockProducerFactory>().InitBlockProducer();
        IBlockProducerRunner runner = container.Resolve<IBlockProducerRunnerFactory>().InitBlockProducerRunner(blockProducer);

        Assert.That(blockProducer, Is.InstanceOf<MergeBlockProducer>());
        Assert.That(((MergeBlockProducer)blockProducer).PreMergeBlockProducer, Is.InstanceOf<AuRaBlockProducer>());
        Assert.That(runner, Is.InstanceOf<MergeBlockProducerRunner>());
    }
}
