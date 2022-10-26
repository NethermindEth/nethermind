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
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;

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

        public override Task<IBlockProducer> InitBlockProducer(IConsensusPlugin consensusPlugin)
        {
            _api.BlockProducerEnvFactory = new AuRaMergeBlockProducerEnvFactory(
                (AuRaNethermindApi)_api,
                _api.Config<IAuraConfig>(),
                _api.DisposeStack,
                _api.DbProvider!,
                _api.BlockTree!,
                _api.ReadOnlyTrieStore!,
                _api.SpecProvider!,
                _api.BlockValidator!,
                _api.RewardCalculatorSource!,
                _api.ReceiptStorage!,
                _api.BlockPreprocessor!,
                _api.TxPool!,
                _api.TransactionComparerProvider!,
                _api.Config<IMiningConfig>(),
                _api.LogManager);

            return base.InitBlockProducer(consensusPlugin);
        }

        protected override PostMergeBlockProducerFactory CreateBlockProducerFactory()
            => new AuRaPostMergeBlockProducerFactory(
                _api.SpecProvider!,
                _api.SealEngine,
                _manualTimestamper!,
                _miningConfig,
                _api.LogManager);

        private bool ShouldBeEnabled(INethermindApi api) => _mergeConfig.Enabled && IsPreMergeConsensusAuRa(api);

        public bool ShouldRunSteps(INethermindApi api)
        {
            _mergeConfig = api.Config<IMergeConfig>();
            return ShouldBeEnabled(api);
        }
    }
}
