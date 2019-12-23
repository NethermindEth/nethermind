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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Storage;

namespace Nethermind.BeaconNode
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class BeaconNodeWorker : BackgroundService
    {
        private const string ConfigKey = "config";

        private readonly IConfiguration _configuration;
        private readonly ClientVersion _clientVersion;
        private readonly IStoreProvider _storeProvider;
        private readonly ForkChoice _forkChoice;
        private readonly INodeStart _nodeStart;
        private readonly ILogger _logger;
        private readonly IClock _clock;
        private readonly IHostEnvironment _environment;
        private bool _stopped;

        public BeaconNodeWorker(ILogger<BeaconNodeWorker> logger,
            IClock clock,
            IHostEnvironment environment,
            IConfiguration configuration,
            ClientVersion clientVersion,
            IStoreProvider storeProvider,
            ForkChoice forkChoice,
            INodeStart nodeStart)
        {
            _logger = logger;
            _clock = clock;
            _environment = environment;
            _configuration = configuration;
            _clientVersion = clientVersion;
            _storeProvider = storeProvider;
            _forkChoice = forkChoice;
            _nodeStart = nodeStart;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Worker stopping.");
            _stopped = true;
            await base.StopAsync(cancellationToken);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Worker starting.");
            await base.StartAsync(cancellationToken);
            _logger.LogDebug("Worker started.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            string version = _clientVersion.Description;
            string environmentName = _environment.EnvironmentName;
            string configName = _configuration[ConfigKey];
            _logger.LogInformation(Event.WorkerStarted, "{ProductTokenVersion} started; {Environment} environment (config '{Config}') [{ThreadId}]",
                version, environmentName, configName, Thread.CurrentThread.ManagedThreadId);

            await _nodeStart.InitializeNodeAsync();

            IStore? store = null;
            while (!stoppingToken.IsCancellationRequested && !_stopped)
            {
                DateTimeOffset time = _clock.UtcNow();
                if (store == null)
                {
                    if (_storeProvider.TryGetStore(out store))
                    {
                        _logger.LogInformation(Event.WorkerStoreAvailableTickStarted, "Store available with genesis time {GenesisTime}, starting clock tick [{ThreadId}]",
                            store!.GenesisTime, Thread.CurrentThread.ManagedThreadId);
                    }
                }
                if (store != null)
                {
                    _forkChoice.OnTick(store, (ulong)time.ToUnixTimeSeconds());
                }
                // Wait for remaining time, if any
                // NOTE: To fast forward time during testing, have the second call to test _clock.Now() jump forward to avoid waiting.
                TimeSpan remaining = time.AddSeconds(1) - _clock.UtcNow();
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, stoppingToken);
                }
            }

            _logger.LogDebug("Worker execute thread exiting [{ThreadId}].", Thread.CurrentThread.ManagedThreadId);
        }
    }
}
