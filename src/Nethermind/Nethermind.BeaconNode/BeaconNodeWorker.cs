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
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Services;
using Nethermind.Core2;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class BeaconNodeWorker : BackgroundService
    {
        private const string ConfigKey = "config";

        private readonly IConfiguration _configuration;
        private readonly IClientVersion _clientVersion;
        private readonly IStoreProvider _storeProvider;
        private readonly ForkChoice _forkChoice;
        private readonly INodeStart _nodeStart;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;
        private readonly IClock _clock;
        private readonly IHostEnvironment _environment;
        private bool _stopped;

        public BeaconNodeWorker(ILogger<BeaconNodeWorker> logger,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IClock clock,
            IHostEnvironment environment,
            IConfiguration configuration,
            IClientVersion clientVersion,
            IStoreProvider storeProvider,
            ForkChoice forkChoice,
            INodeStart nodeStart)
        {
            _logger = logger;
            _timeParameterOptions = timeParameterOptions;
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
            if (_logger.IsDebug()) LogDebug.BeaconNodeWorkerStopping(_logger, null);
            _stopped = true;
            await base.StopAsync(cancellationToken);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.BeaconNodeWorkerStarting(_logger, null);
            await base.StartAsync(cancellationToken);
            if (_logger.IsDebug()) LogDebug.BeaconNodeWorkerStarted(_logger, null);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_logger.IsInfo())
                Log.BeaconNodeWorkerExecuteStarted(_logger, _clientVersion.Description, _environment.EnvironmentName,
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
                        ulong time = (ulong) clockTime.ToUnixTimeSeconds();
                        
                        if (store == null)
                        {
                            if (_storeProvider.TryGetStore(out store))
                            {
                                if (_logger.IsInfo())
                                {
                                    long slotValue = ((long)time - (long)store!.GenesisTime) / _timeParameterOptions.CurrentValue.SecondsPerSlot;
                                    Log.WorkerStoreAvailableTickStarted(_logger, store!.GenesisTime, time, slotValue,
                                        Thread.CurrentThread.ManagedThreadId, null);
                                }
                            }
                        }

                        if (store != null )
                        {
                            if (time >= store.GenesisTime)
                            {
                                await _forkChoice.OnTickAsync(store, time);
                            }
                            else
                            {
                                long timeToGenesis = (long)store.GenesisTime - (long)time;
                                if (timeToGenesis < 10 || timeToGenesis % 10 == 0)
                                {
                                    if (_logger.IsInfo()) Log.GenesisCountdown(_logger, timeToGenesis, null);
                                }
                            }
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
                    catch (TaskCanceledException)
                    {
                        // This is expected when exiting
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

            if (_logger.IsDebug()) LogDebug.BeaconNodeWorkerExecuteExiting(_logger, Thread.CurrentThread.ManagedThreadId, null);
        }
    }
}
