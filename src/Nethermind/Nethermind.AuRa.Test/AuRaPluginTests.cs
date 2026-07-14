// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaPluginTests
    {
        [Test]
        public void Can_wire_block_producer_from_container()
        {
            ChainSpec chainSpec = new()
            {
                SealEngineType = Core.SealEngineType.AuRa,
                Parameters = new ChainParameters(),
                Allocations = [],
                Genesis = Build.A.Block.WithDifficulty(0).WithAura(0, new byte[65]).WithBlobGasUsed(0).TestObject,
                EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                    new AuRaChainSpecEngineParameters
                    {
                        StepDuration = { { 0, 3 } },
                        ValidatorsJson = new AuRaChainSpecEngineParameters.AuRaValidatorJson { List = [Address.Zero] }
                    })
            };

            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(chainSpec))
                .AddModule(new AuRaModule(chainSpec))
                .AddSingleton<NethermindApi.Dependencies>()
                .AddSingleton<IReportingValidator>(NullReportingValidator.Instance)
                .AddSingleton<IBlockProcessingQueue>(Substitute.For<IBlockProcessingQueue>())
                .AddSingleton<IAuRaBlockFinalizationManager>(Substitute.For<IAuRaBlockFinalizationManager>())
                .Build();

            IBlockProducer blockProducer = container.Resolve<IBlockProducerFactory>().InitBlockProducer();
            IBlockProducerRunner runner = container.Resolve<IBlockProducerRunnerFactory>().InitBlockProducerRunner(blockProducer);

            Assert.That(blockProducer, Is.InstanceOf<AuRaBlockProducer>());
            Assert.That(runner, Is.InstanceOf<StandardBlockProducerRunner>());
        }

        [Test]
        public void ApplyToReleaseSpec_sets_Eip158IgnoredAccount()
        {
            AuRaChainSpecEngineParameters parameters = new();
            ReleaseSpec spec = new();

            parameters.ApplyToReleaseSpec(spec, 0, null);

            Assert.That(spec.Eip158IgnoredAccount, Is.EqualTo(Address.SystemUser));
        }

    }
}
