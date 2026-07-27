// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaPluginTests
    {
        [Test]
        public void Can_wire_block_producer_from_container()
        {
            ChainSpec chainSpec = CreateChainSpec();

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
        public void Can_resolve_reporting_validator_when_main_block_processor_is_decorated()
        {
            ChainSpec chainSpec = CreateChainSpec(useMultiValidator: true);

            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(chainSpec))
                .AddModule(new AuRaModule(chainSpec))
                .AddSingleton<NethermindApi.Dependencies>()
                .AddDecorator<AuRaNethermindApi>((ctx, api) =>
                {
                    api.TxPool = ctx.Resolve<ITxPool>();
                    return api;
                })
                .AddSingleton<IMainProcessingModule>(new BlockProcessorDecoratingModule())
                .AddSingleton<IBlockProcessingQueue>(Substitute.For<IBlockProcessingQueue>())
                .AddSingleton<IAuRaBlockFinalizationManager>(Substitute.For<IAuRaBlockFinalizationManager>())
                .Build();

            IReportingValidator reportingValidator = container.Resolve<IReportingValidator>();

            Assert.That(reportingValidator, Is.InstanceOf<MultiValidator>());
        }

        [Test]
        public void ApplyToReleaseSpec_sets_Eip158IgnoredAccount()
        {
            AuRaChainSpecEngineParameters parameters = new();
            ReleaseSpec spec = new();

            parameters.ApplyToReleaseSpec(spec, 0, null);

            Assert.That(spec.Eip158IgnoredAccount, Is.EqualTo(Address.SystemUser));
        }

        private static ChainSpec CreateChainSpec(bool useMultiValidator = false) => new()
        {
            SealEngineType = Core.SealEngineType.AuRa,
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.WithDifficulty(0).WithAura(0, new byte[65]).WithBlobGasUsed(0).TestObject,
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new AuRaChainSpecEngineParameters
                {
                    StepDuration = { { 0, 3 } },
                    ValidatorsJson = useMultiValidator
                        ? new AuRaChainSpecEngineParameters.AuRaValidatorJson
                        {
                            Multi =
                            {
                                [0] = new AuRaChainSpecEngineParameters.AuRaValidatorJson { List = [Address.Zero] }
                            }
                        }
                        : new AuRaChainSpecEngineParameters.AuRaValidatorJson { List = [Address.Zero] }
                })
        };

        private sealed class BlockProcessorDecoratingModule : Module, IMainProcessingModule
        {
            protected override void Load(ContainerBuilder builder) =>
                builder.AddDecorator<IBlockProcessor>((_, _) => Substitute.For<IBlockProcessor>());
        }

    }
}
