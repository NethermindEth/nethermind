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
using Nethermind.Logging.Microsoft;

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
            if (_logger.IsDebug()) LogDebug.WorkerStopping(_logger, null);
            _stopped = true;
            await base.StopAsync(cancellationToken);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.WorkerStarting(_logger, null);
            await base.StartAsync(cancellationToken);
            if (_logger.IsDebug()) LogDebug.WorkerStarted(_logger, null);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_logger.IsInfo())
                Log.WorkerExecuteStarted(_logger, _clientVersion.Description, _environment.EnvironmentName,
                    _configuration[ConfigKey], Thread.CurrentThread.ManagedThreadId, null);

            try
            {
                await _nodeStart.InitializeNodeAsync();

                IStore? store = null;
                while (!stoppingToken.IsCancellationRequested && !_stopped)
                {
                    try
                    {
                        DateTimeOffset clockTime = _clock.UtcNow();
                        if (store == null)
                        {
                            if (_storeProvider.TryGetStore(out store))
                            {
                                if (_logger.IsInfo())
                                    Log.WorkerStoreAvailableTickStarted(_logger, store!.GenesisTime,
                                        Thread.CurrentThread.ManagedThreadId, null);
                            }
                        }

                        ulong time = (ulong) clockTime.ToUnixTimeSeconds();
                        if (store != null)
                        {
                            await _forkChoice.OnTickAsync(store, time);
                        }

                        // Wait for remaining time, if any
                        // NOTE: To fast forward time during testing, have the second call to test _clock.Now() jump forward to avoid waiting.
                        DateTimeOffset nextClockTime = DateTimeOffset.FromUnixTimeSeconds((long) time + 1);
                        TimeSpan remaining = nextClockTime - _clock.UtcNow();
                        if (remaining > TimeSpan.Zero)
                        {
                            await Task.Delay(remaining, stoppingToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsError()) Log.BeaconNodeWorkerLoopError(_logger, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.BeaconNodeWorkerCriticalError(_logger, ex);
                throw;
            }

            if (_logger.IsDebug()) LogDebug.WorkerExecuteExiting(_logger, Thread.CurrentThread.ManagedThreadId, null);
        }
    }
}
