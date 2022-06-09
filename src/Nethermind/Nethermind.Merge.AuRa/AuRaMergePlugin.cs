using Nethermind.Merge.Plugin;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Processing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.State;
using Nethermind.Specs.ChainSpecStyle;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;

namespace Nethermind.Merge.AuRa
{
    public class AuRaMergePlugin : MergePlugin, IInitializationPlugin
    {
        private AuRaNethermindApi? _auraApi;
        private IAuRaMergeConfig? _auraMergeConfig;

        public override bool MergeEnabled => _auraMergeConfig!.Enabled;

        public override async Task Init(INethermindApi nethermindApi)
        {
            _auraMergeConfig = nethermindApi.Config<IAuRaMergeConfig>();
            if (_auraMergeConfig.Enabled)
            {
                _mergeConfig.Enabled = false; // set MergePlugin as disabled
                await base.Init(nethermindApi);
                _auraApi = (AuRaNethermindApi)nethermindApi;
                _auraApi.PoSSwitcher = _poSSwitcher;
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

        public bool ShouldRunSteps(INethermindApi api) =>
            api.Config<IAuRaMergeConfig>().Enabled;
    }
}
