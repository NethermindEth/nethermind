// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.JsonRpc.Modules;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Clique
{
    public class CliquePlugin(ChainSpec chainSpec) : IConsensusPlugin
    {
        public string Name => SealEngineType;

        public string Description => $"{SealEngineType} Consensus Engine";

        public string Author => "Nethermind";

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;


        public string SealEngineType => Nethermind.Core.SealEngineType.Clique;

        public IModule Module => new CliqueModule();
    }

    public class CliqueModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder
                .Map<CliqueChainSpecEngineParameters, ChainSpec>(chainSpec =>
                    chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<CliqueChainSpecEngineParameters>())

                .AddDecorator<ICliqueConfig>((ctx, cfg) =>
                {
                    CliqueChainSpecEngineParameters param = ctx.Resolve<CliqueChainSpecEngineParameters>();
                    cfg.BlockPeriod = param.Period;
                    cfg.Epoch = param.Epoch;

                    return cfg;
                })

                .AddSingleton<ISnapshotManager, SnapshotManager>()
                .AddLast<IBlockPreprocessorStep, AuthorRecoveryStep>()
                .AddSingleton<ISealValidator, CliqueSealValidator>()
                .AddSingleton<ISealer, CliqueSealer>()

                .AddSingleton<CliqueBlockProducerFactory>()
                .Bind<IBlockProducerFactory, CliqueBlockProducerFactory>()
                .Bind<IBlockProducerRunnerFactory, CliqueBlockProducerFactory>()

                .AddSingleton<IHealthHintService, CliqueHealthHintService>()

                .RegisterSingletonJsonRpcModule<ICliqueRpcModule, CliqueRpcModule>()
                ;
        }
    }
}
