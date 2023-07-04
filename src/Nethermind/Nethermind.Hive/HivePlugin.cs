// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;

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

        public async Task InitNetworkProtocol()
        {
            if (Enabled)
            {
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.BlockProcessingQueue is null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
                if (_api.ConfigProvider is null) throw new ArgumentNullException(nameof(_api.ConfigProvider));
                if (_api.LogManager is null) throw new ArgumentNullException(nameof(_api.LogManager));
                if (_api.FileSystem is null) throw new ArgumentNullException(nameof(_api.FileSystem));
                if (_api.BlockValidator is null) throw new ArgumentNullException(nameof(_api.BlockValidator));

                _api.TxGossipPolicy.Policies.Clear();

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
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }

        private bool Enabled { get; set; }
    }
}
