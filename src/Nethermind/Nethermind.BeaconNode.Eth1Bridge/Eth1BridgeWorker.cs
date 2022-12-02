// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Eth1Bridge
{
    public class Eth1BridgeWorker : BackgroundService
    {
        private readonly IOptionsMonitor<AnchorState> _anchorStateOptions;
        private readonly IClientVersion _clientVersion;
        private readonly IHostEnvironment _environment;
        private readonly IEth1Genesis _eth1Genesis;
        private readonly IDepositStore _depositStore;
        private readonly IEth1GenesisProvider _eth1GenesisProvider;
        private static readonly TimeSpan _genesisCheckDelay = TimeSpan.FromSeconds(5);
        private readonly ILogger _logger;

        public Eth1BridgeWorker(ILogger<Eth1BridgeWorker> logger,
            IHostEnvironment environment,
            IOptionsMonitor<AnchorState> anchorStateOptions,
            IClientVersion clientVersion,
            IEth1GenesisProvider eth1GenesisProvider,
            IEth1Genesis eth1Genesis,
            IDepositStore depositStore)
        {
            _logger = logger;
            _environment = environment;
            _anchorStateOptions = anchorStateOptions;
            _clientVersion = clientVersion;
            _eth1GenesisProvider = eth1GenesisProvider;
            _eth1Genesis = eth1Genesis;
            _depositStore = depositStore;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_logger.IsDebug()) LogDebug.PeeringWorkerExecute(_logger, null);

            if (_anchorStateOptions.CurrentValue.Source == AnchorStateSource.Eth1Genesis)
            {
                await ExecuteEth1GenesisAsync(stoppingToken);
            }

            // TODO: Any other work for the Eth1Bridge, e.g. maybe need IEth1Collection or similar interface that needs to be run
        }

        public async Task ExecuteEth1GenesisAsync(CancellationToken stoppingToken)
        {
            int count = 1;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    LogDebug.CheckingForEth1Genesis(_logger, count, null);

                var eth1GenesisData = await _eth1GenesisProvider.GetEth1GenesisDataAsync(stoppingToken)
                    .ConfigureAwait(false);
                var genesisSuccess = await _eth1Genesis.TryGenesisAsync(
                    eth1GenesisData.BlockHash,
                    eth1GenesisData.Timestamp).ConfigureAwait(false);
                if (genesisSuccess)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                        Log.Eth1GenesisSuccess(_logger, eth1GenesisData.BlockHash, eth1GenesisData.Timestamp,
                            (uint)_depositStore.Deposits.Count, count, null);
                    break;
                }

                await Task.Delay(_genesisCheckDelay);
                count++;
            }
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsInfo())
                Log.PeeringWorkerStarting(_logger, _clientVersion.Description,
                    _environment.EnvironmentName, Thread.CurrentThread.ManagedThreadId, null);

            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.PeeringWorkerStopping(_logger, null);

            await base.StopAsync(cancellationToken);
        }
    }
}
