using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
            if (Enabled)
            {
                if (_api.SyncPeerPool == null) throw new ArgumentNullException(nameof(_api.SyncPeerPool));

                _api.SyncPeerPool.PeerRefreshed += OnPeerRefreshed;
            }

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
                if (_api.BlockProcessingQueue == null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
                if (_api.ConfigProvider == null) throw new ArgumentNullException(nameof(_api.ConfigProvider));
                if (_api.LogManager == null) throw new ArgumentNullException(nameof(_api.LogManager));
                if (_api.FileSystem == null) throw new ArgumentNullException(nameof(_api.FileSystem));
                if (_api.BlockValidator == null) throw new ArgumentNullException(nameof(_api.BlockValidator));

                HiveRunner hiveRunner = new(
                    _api.BlockTree,
                    _api.BlockProcessingQueue,
                    _api.ConfigProvider,
                    _api.LogManager.GetClassLogger(),
                    _api.FileSystem,
                    _api.BlockValidator
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
