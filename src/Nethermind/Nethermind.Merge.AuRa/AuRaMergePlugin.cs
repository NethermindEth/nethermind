// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Contracts;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Network;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Repositories;

namespace Nethermind.Merge.AuRa
{
    /// <summary>
    /// Plugin for AuRa -> PoS migration
    /// </summary>
    /// <remarks>IMPORTANT: this plugin should always come before MergePlugin</remarks>
    public class AuRaMergePlugin(ChainSpec chainSpec, IMergeConfig mergeConfig) : INethermindPlugin
    {
        public string Name => "AuRaMerge";
        public string Description => "AuRa Merge plugin for ETH1-ETH2";
        public string Author => "Nethermind";
        public bool Enabled => mergeConfig.Enabled && chainSpec.SealEngineType == SealEngineType.AuRa;
        public bool MustInitialize => true;
        public IModule Module => new AuRaMergeModule();
    }

    /// <summary>
    /// Note: <see cref="AuRaMergeModule"/> is applied also when <see cref="AuRaModule"/> is applied.
    /// </summary>
    public class AuRaMergeModule : Module
    {
        protected override void Load(ContainerBuilder builder) => builder
                .AddModule(new BaseMergePluginModule())

                .AddLast<IP2PCapabilityResolver, MergeP2PCapabilityResolver>()

                // The post-merge env uses the plain AuRa tx pool source (no posdao/randomness txs).
                .Bind<IBlockProducerTxSourceFactory, AuRaTxPoolTxSourceFactory>()

                // Post-merge block production decorates the AuRa engine factory (from AuRaModule).
                .AddSingleton<ManualTimestamper>()
                .AddSingleton<PostMergeBlockProducerFactory, ISpecProvider, ISealEngine, ManualTimestamper, IBlocksConfig, ILogManager, IInclusionListTxSource>(
                    (specProvider, sealEngine, timestamper, blocksConfig, logManager, inclusionListTxSource) =>
                        new AuRaPostMergeBlockProducerFactory(specProvider, sealEngine, timestamper, blocksConfig, logManager, inclusionListTxSource: inclusionListTxSource))
                .AddDecorator<IBlockProducerFactory, MergeBlockProducerFactory>()
                .AddDecorator<IBlockProducerRunnerFactory, MergeBlockProducerRunnerFactory>()
                .AddDecorator<IBlockProductionPolicy, MergeBlockProductionPolicy>()

                .AddSingleton<IWithdrawalContractFactory, WithdrawalContractFactory>()
                .AddScoped<IWithdrawalContract, IWithdrawalContractFactory, ITransactionProcessor>((factory, txProcessor) => factory.Create(txProcessor))
                .AddScoped<IWithdrawalProcessor, AuraWithdrawalProcessor>()
                .AddSingleton<IWithdrawalProcessorFactory, AuraWithdrawalProcessorFactory>()
                .AddScoped<IBlockProcessor, AuRaMergeBlockProcessor>()

                .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
                .AddDecorator<IUnclesValidator, MergeUnclesValidator>()
                .AddDecorator<ISealValidator, MergeSealValidator>()
                .AddDecorator<ISealer, MergeSealer>()

                .AddDecorator<IGossipPolicy, MergeGossipPolicy>()

                // Overrides AuRaModule's registration. This relies on the plugin order (Aura before AuRaMerge in the
                // default Plugin.PluginOrder), so that this registration is the last one and wins.
                // On a post-merge head, skip only the startup backward ChainLevelInfo scan for the last AuRa-finalized
                // level: it would read every level back to the merge transition, or down to genesis on post-merge
                // synced nodes, and its result no longer selects the active validator (post-merge blocks bypass the
                // AuRa validator, the branch processor is not wired and the manager is disposed at startup). Later
                // queries such as GetFinalizationLevel, used when re-executing historical blocks, still work.
                // Pre-merge heads (archive sync from genesis) keep the eager load MultiValidator depends on.
                .AddSingleton<IAuRaBlockFinalizationManager, IBlockTree, IChainLevelInfoRepository, IValidatorStore, ILogManager, AuRaChainSpecEngineParameters, IPoSSwitcher>(
                    static (blockTree, chainLevelInfoRepository, validatorStore, logManager, param, poSSwitcher) =>
                        new AuRaBlockFinalizationManager(blockTree, chainLevelInfoRepository, validatorStore, logManager, param.TwoThirdsMajorityTransition,
                            loadLastFinalizedBlockLevel: !poSSwitcher.IsHeadPostMerge(blockTree)))

                // Disposes the AuRa finalization manager at the merge transition. Resolved eagerly in
                // InitializeBlockchainAuRaMerge for its constructor side-effect; Autofac owns disposal.
                .AddSingleton<AuRaTerminalBlockDisposer>()

                // Merge-aware override: skips wiring the branch processor on post-merge chains so
                // the AuRa finalization manager's startup catch-up walk never runs.
                .AddStep(typeof(InitializeBlockchainAuRaMerge))

                .AddStep(typeof(InitializeAuRaMergePlugin))

                .ResolveOnServiceActivation<ProcessedTransactionsDbCleaner, IBlockTree>()
                ;
    }
}
