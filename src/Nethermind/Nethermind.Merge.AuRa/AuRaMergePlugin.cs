// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Merge.Plugin;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Db;
using Nethermind.Blockchain;
using Nethermind.State;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.AuRa.Transactions;

namespace Nethermind.Merge.AuRa
{
    /// <summary>
    /// Plugin for AuRa -> PoS migration
    /// </summary>
    /// <remarks>IMPORTANT: this plugin should always come before MergePlugin</remarks>
    public class AuRaMergePlugin : MergePlugin, IInitializationPlugin
    {
        private AuRaNethermindApi? _auraApi;

        public override string Name => "AuRaMerge";
        public override string Description => $"AuRa Merge plugin for ETH1-ETH2";

        public override bool MergeEnabled => ShouldBeEnabled(_api);

        public override async Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _mergeConfig = nethermindApi.Config<IMergeConfig>();
            if (MergeEnabled)
            {
                await base.Init(nethermindApi);
                _auraApi = (AuRaNethermindApi)nethermindApi;
                _auraApi.PoSSwitcher = _poSSwitcher;

                // this runs before all init steps that use tx filters
                TxAuRaFilterBuilders.CreateFilter = (originalFilter, fallbackFilter) =>
                    originalFilter is MinGasPriceContractTxFilter ? originalFilter
                    : new AuRaMergeTxFilter(_poSSwitcher, originalFilter, fallbackFilter);
            }
        }

        protected override ITxSource? CreateTxSource(IStateProvider stateProvider)
        {
            ReadOnlyTxProcessingEnv txProcessingEnv = new(
                _api.DbProvider!.AsReadOnly(false),
                _api.ReadOnlyTrieStore,
                _api.BlockTree!.AsReadOnly(),
                _api.SpecProvider,
                _api.LogManager
            );

            ReadOnlyTxProcessingEnv constantContractsProcessingEnv = new(
                _api.DbProvider!.AsReadOnly(false),
                _api.ReadOnlyTrieStore,
                _api.BlockTree!.AsReadOnly(),
                _api.SpecProvider,
                _api.LogManager
            );

            return new StartBlockProducerAuRa(_auraApi!)
                .CreateStandardTxSourceForProducer(txProcessingEnv, constantContractsProcessingEnv);
        }

        private bool ShouldBeEnabled(INethermindApi api) => _mergeConfig.Enabled && IsPreMergeConsensusAuRa(api);

        public bool ShouldRunSteps(INethermindApi api)
        {
            _mergeConfig = api.Config<IMergeConfig>();
            return ShouldBeEnabled(api);
        }
    }
}
