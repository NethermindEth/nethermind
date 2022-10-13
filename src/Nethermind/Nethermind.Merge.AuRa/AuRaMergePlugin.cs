//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

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
