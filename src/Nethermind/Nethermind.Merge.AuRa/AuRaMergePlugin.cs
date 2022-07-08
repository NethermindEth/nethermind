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
        private IAuRaMergeConfig? _auraMergeConfig;

        public override bool MergeEnabled => _auraMergeConfig!.Enabled;

        public override async Task Init(INethermindApi nethermindApi)
        {
            _auraMergeConfig = nethermindApi.Config<IAuRaMergeConfig>();
            if (_auraMergeConfig.Enabled)
            {
                await base.Init(nethermindApi);
                _auraApi = (AuRaNethermindApi)nethermindApi;
                _auraApi.PoSSwitcher = _poSSwitcher;
                _mergeConfig.Enabled = false; // set MergePlugin as disabled

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

        public bool ShouldRunSteps(INethermindApi api) => api.Config<IAuRaMergeConfig>().Enabled;
    }
}
