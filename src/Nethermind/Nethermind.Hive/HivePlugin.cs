using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Hive
{
    public class HivePlugin : INethermindPlugin
    {
        private INethermindApi _api = null!;
        private IHiveConfig _hiveConfig = null!;
        private ILogger _logger = null!;
        private readonly CancellationTokenSource _disposeCancellationToken = new();

        public ValueTask DisposeAsync()
        {
            _disposeCancellationToken.Cancel();
            _disposeCancellationToken.Dispose();
            return ValueTask.CompletedTask;
        }

        public string Name => "Hive";
        
        public string Description => "Plugin used for executing Hive Ethereum Tests";
        
        public string Author => "Nethermind";
        
        public Task Init(INethermindApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _hiveConfig = _api.ConfigProvider.GetConfig<IHiveConfig>();
            _logger = _api.LogManager.GetClassLogger();

            Enabled = Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true" || _hiveConfig.Enabled;

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            if (_api.SyncPeerPool == null) throw new ArgumentNullException(nameof(_api.SyncPeerPool));

            _api.SyncPeerPool.PeerRefreshed += OnPeerRefreshed;
            return Task.CompletedTask;
        }

        private void OnPeerRefreshed(object? sender, PeerHeadRefreshedEventArgs e)
        {
            BlockHeader header = e.Header;
            if (header.UnclesHash == Keccak.OfAnEmptySequenceRlp && header.TxRoot == Keccak.EmptyTreeHash)
            {
                Block block = new(header, new BlockBody());
                _api.BlockTree!.SuggestBlock(block);
            }
        }

        public async Task InitRpcModules()
        { 
            if (Enabled)
            {
                if (_api.BlockTree == null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.ReceiptStorage == null) throw new ArgumentNullException(nameof(_api.ReceiptStorage));
                if (_api.SpecProvider == null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.DbProvider == null) throw new ArgumentNullException(nameof(_api.DbProvider));
                if (_api.BlockValidator == null) throw new ArgumentNullException(nameof(_api.BlockValidator));

                ReadOnlyDbProvider readonlyDbProvider = _api.DbProvider.AsReadOnly(false);
                ReadOnlyTxProcessingEnv txProcessingEnv =
                    new(readonlyDbProvider, _api.ReadOnlyTrieStore, _api.BlockTree.AsReadOnly(), _api.SpecProvider,
                        _api.LogManager);

                IRewardCalculator rewardCalculator =
                    _api.RewardCalculatorSource!.Get(txProcessingEnv.TransactionProcessor);

                ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                    txProcessingEnv,
                    _api.BlockValidator,
                    _api.BlockPreprocessor,
                    rewardCalculator,
                    _api.ReceiptStorage,
                    readonlyDbProvider,
                    _api.SpecProvider,
                    _api.LogManager);

                Tracer tracer = new(chainProcessingEnv.StateProvider, chainProcessingEnv.ChainProcessor,
                    ProcessingOptions.DoNotUpdateHead | ProcessingOptions.ReadOnlyChain);

                HiveRunner hiveRunner = new(
                    _api.BlockTree,
                    _api.ConfigProvider,
                    _api.LogManager.GetClassLogger(),
                    _api.FileSystem,
                    _api.BlockValidator,
                    tracer
                );
                
                if (_logger.IsInfo) _logger.Info("Hive is starting");
                
                await hiveRunner.Start(_disposeCancellationToken.Token);
            }
            else
            {
                if (_logger.IsInfo) _logger.Info("Skipping Hive plugin");
            }
        }

        private bool Enabled { get; set; }
    }
}
