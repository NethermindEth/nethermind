// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Merge.AuRa.InitializationSteps;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
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

        public override IBlockProducer InitBlockProducer(IBlockProducerFactory consensusPlugin, ITxSource? txSource)
        {
            _api.BlockProducerEnvFactory = new AuRaMergeBlockProducerEnvFactory(
                _auraApi!.ChainSpec,
                _auraApi.AbiEncoder,
                _auraApi.CreateStartBlockProducer,
                _api.ReadOnlyTxProcessingEnvFactory,
                _api.WorldStateManager!,
                _api.BlockTree!,
                _api.SpecProvider!,
                _api.BlockValidator!,
                _api.RewardCalculatorSource!,
                _api.ReceiptStorage!,
                _api.BlockPreprocessor!,
                _api.TxPool!,
                _api.TransactionComparerProvider!,
                _api.Config<IBlocksConfig>(),
                _api.LogManager);

            return base.InitBlockProducer(consensusPlugin, txSource);
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
            return new AuRaMergeFinalizationManager(_blockFinalizationManager,
                _auraApi!.FinalizationManager ??
                throw new ArgumentNullException(nameof(_auraApi.FinalizationManager),
                    "Cannot instantiate AuRaMergeFinalizationManager when AuRaFinalizationManager is null!"),
                _poSSwitcher);
        }

        public override IEnumerable<StepInfo> GetSteps()
        {
            yield return typeof(InitializeBlockchainAuRaMerge);
            yield return typeof(RegisterAuRaMergeRpcModules);
        }
    }
}
