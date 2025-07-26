// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Merge.AuRa.InitializationSteps;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Merge.AuRa
{
    /// <summary>
    /// Plugin for AuRa -> PoS migration
    /// </summary>
    /// <remarks>IMPORTANT: this plugin should always come before MergePlugin</remarks>
    public class AuRaMergePlugin(ChainSpec chainSpec, IMergeConfig mergeConfig) : MergePlugin(chainSpec, mergeConfig)
    {
        private AuRaNethermindApi? _auraApi;
        private readonly IMergeConfig _mergeConfig = mergeConfig;
        private readonly ChainSpec _chainSpec = chainSpec;

        public override string Name => "AuRaMerge";
        public override string Description => "AuRa Merge plugin for ETH1-ETH2";
        protected override bool MergeEnabled => _mergeConfig.Enabled && _chainSpec.SealEngineType == SealEngineType.AuRa;

        public override async Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            if (MergeEnabled)
            {
                await base.Init(nethermindApi);
                _auraApi = (AuRaNethermindApi)nethermindApi;

                // this runs before all init steps that use tx filters
                TxAuRaFilterBuilders.CreateFilter = (originalFilter, fallbackFilter) =>
                    originalFilter is MinGasPriceContractTxFilter ? originalFilter
                    : new AuRaMergeTxFilter(_poSSwitcher, originalFilter, fallbackFilter);
            }
        }

        protected override PostMergeBlockProducerFactory CreateBlockProducerFactory()
            => new AuRaPostMergeBlockProducerFactory(
                _api.SpecProvider!,
                _api.SealEngine,
                _manualTimestamper!,
                _blocksConfig,
                _api.LogManager);

        protected override IBlockFinalizationManager InitializeMergeFinilizationManager()
        {
            return new AuRaMergeFinalizationManager(_api.Context.Resolve<IManualBlockFinalizationManager>(),
                _auraApi!.FinalizationManager ??
                throw new ArgumentNullException(nameof(_auraApi.FinalizationManager),
                    "Cannot instantiate AuRaMergeFinalizationManager when AuRaFinalizationManager is null!"),
                _poSSwitcher);
        }

        public override IModule Module => new AuRaMergeModule();
    }

    /// <summary>
    /// Note: <see cref="AuRaMergeModule"/> is applied also when <see cref="AuRaModule"/> is applied.
    /// Note: <see cref="AuRaMergePlugin"/> subclasses <see cref="MergePlugin"/>, but some component that is set
    /// in <see cref="MergePlugin"/> is replaced later by standard AuRa components.
    /// </summary>
    public class AuRaMergeModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder
                .AddModule(new BaseMergePluginModule())

                // Aura (non merge) use `BlockProducerStarter` directly.
                .AddSingleton<IBlockProducerEnvFactory, AuRaMergeBlockProducerEnvFactory>()
                .AddSingleton<IBlockProducerTxSourceFactory, AuRaMergeBlockProducerTxSourceFactory>()

                .AddSingleton<IAuRaBlockProcessorFactory, AuRaMergeBlockProcessorFactory>()

                .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
                .AddDecorator<IUnclesValidator, MergeUnclesValidator>()
                .AddDecorator<ISealValidator, MergeSealValidator>()
                .AddDecorator<ISealer, MergeSealer>()
                ;
        }
    }
}
