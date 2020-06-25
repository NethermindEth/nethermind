//  Copyright (c) 2018 Demerzel Solutions Limited
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