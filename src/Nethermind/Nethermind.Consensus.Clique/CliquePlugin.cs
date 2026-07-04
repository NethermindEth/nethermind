// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Services;
using Nethermind.Core;
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

        public Task Init(INethermindApi nethermindApi)
        {
            (IApiWithStores _, IApiWithBlockchain setInApi) = nethermindApi.ForInit;

            ISnapshotManager snapshotManager = nethermindApi.Context.Resolve<ISnapshotManager>();

            setInApi.BlockPreprocessor.AddLast(new AuthorRecoveryStep(snapshotManager));

            return Task.CompletedTask;
        }

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
                .AddSingleton<ISealValidator, CliqueSealValidator>()
                .AddSingleton<ISealer, CliqueSealer>()

                .AddSingleton<CliqueBlockProducerFactory>()
                .Bind<IBlockProducerFactory, CliqueBlockProducerFactory>()
                .Bind<IBlockProducerRunnerFactory, CliqueBlockProducerFactory>()

                .AddSingleton<IHealthHintService, CliqueHealthHintService>()

                // Expose the runner the InitializeBlockProducer step sets on the api as a DI service so the RPC
                // module can take it by constructor injection. It is a Clique runner only on a signer node.
                .AddSingleton<IBlockProducerRunner, INethermindApi>(api => api.BlockProducerRunner)
                .RegisterSingletonJsonRpcModule<ICliqueRpcModule, CliqueRpcModule>()
                ;
        }
    }
}
