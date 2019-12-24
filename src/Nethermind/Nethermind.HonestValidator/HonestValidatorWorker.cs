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
using Nethermind.BeaconNode;
using Nethermind.BeaconNode.Services;
using Nethermind.Logging.Microsoft;

namespace Nethermind.HonestValidator
{
    public class HonestValidatorWorker : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly IBeaconNodeApi _beaconNodeApi;
        private readonly ClientVersion _clientVersion;
        private readonly ILogger _logger;
        private readonly IClock _clock;
        private readonly IHostEnvironment _environment;
        private bool _stopped;

        public HonestValidatorWorker(ILogger<HonestValidatorWorker> logger,
            IClock clock,
            IHostEnvironment environment,
            IConfiguration configuration,
            IBeaconNodeApi beaconNodeApi,
            ClientVersion clientVersion)
        {
            _logger = logger;
            _clock = clock;
            _environment = environment;
            _configuration = configuration;
            _beaconNodeApi = beaconNodeApi;
            _clientVersion = clientVersion;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.HonestValidatorWorkerStopping(_logger, null);
            _stopped = true;
            await base.StopAsync(cancellationToken);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.HonestValidatorWorkerStarting(_logger, null);
            await base.StartAsync(cancellationToken);
            if (_logger.IsDebug()) LogDebug.HonestValidatorWorkerStarted(_logger, null);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_logger.IsInfo())
                Log.HonestValidatorWorkerExecuteStarted(_logger, _clientVersion.Description,
                    _environment.EnvironmentName, Thread.CurrentThread.ManagedThreadId, null);

            try
            {
                // Config
                // List of nodes
                // Validator private keys (or quickstart)
                // Seconds per slot
                        
                // The proxy needs to take care of this (i.e. transparent to worker)
                // Not connected: (remote vs local)
                // connect to beacon node (priority order)
                // if not connected, wait and try next
                        
                // Log beacon node connected to
                string nodeVersion = await _beaconNodeApi.GetNodeVersionAsync().ConfigureAwait(false);
                // Check and record genesis time
                ulong genesisTime = await _beaconNodeApi.GetGenesisTimeAsync().ConfigureAwait(false);
                Log.HonestValidatorWorkerConnected(_logger, nodeVersion, genesisTime, null);
                
                while (!stoppingToken.IsCancellationRequested && !_stopped)
                {
                    try
                    {
                        DateTimeOffset clockTime = _clock.UtcNow();
                        ulong time = (ulong) clockTime.ToUnixTimeSeconds();
                        
                        // Check start of each slot
                        // Get duties
                        
                        // If proposer, get block, sign block, return to node
                        // Retry if not successful; need to queue this up to send immediately if connection issue. (or broadcast?)
                        
                        // If upcoming attester, join (or change) topics
                        // Subscribe to topics
                        // Attest 1/3 way through slot

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
                        if (_logger.IsError()) Log.HonestValidatorWorkerLoopError(_logger, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.HonestValidatorWorkerCriticalError(_logger, ex);
                throw;
            }

            if (_logger.IsDebug()) LogDebug.HonestValidatorWorkerExecuteExiting(_logger, Thread.CurrentThread.ManagedThreadId, null);
        }
    }
}