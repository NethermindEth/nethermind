// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Abi;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.AuRa.Services;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Db;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa;

public class AuraModule: Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<AuraHeaderValidatorFactory>()
            .SingleInstance();

        builder.Register<AuraHeaderValidatorFactory, IHeaderValidator>(bStack => bStack.CreateHeaderValidator())
            .As<IHeaderValidator>();

        builder.Register<AuRaParameters, AbiEncoder, IRewardCalculatorSource>((auraParam, abiEncoder) => AuRaRewardCalculator.GetSource(auraParam, abiEncoder))
            .As<IRewardCalculatorSource>();

        builder.Register<IDbProvider, IValidatorStore>(provider => new ValidatorStore(provider.BlockInfosDb))
            .SingleInstance();

        builder.RegisterType<ValidSealerStrategy>()
            .As<IValidSealerStrategy>();

        builder.RegisterType<AuRaStepCalculator>()
            .As<IAuRaStepCalculator>();

        builder.RegisterType<AuRaSealValidator>()
            .As<ISealValidator>();

        builder.Register<ChainSpec, AuRaParameters>((spec) => spec.AuRa)
            .As<AuRaParameters>();

        builder.RegisterType<AuRaSealer>()
            .As<ISealer>();

        builder.RegisterType<AuraHealthHintService>()
            .As<IHealthHintService>();

        builder.RegisterType<AuraBlockchainStack>()
            .AsSelf();

        builder.Register<AuraBlockchainStack, BlockProcessor>((stack) => stack.CreateBlockProcessor())
            .As<IBlockProcessor>();
    }
}
