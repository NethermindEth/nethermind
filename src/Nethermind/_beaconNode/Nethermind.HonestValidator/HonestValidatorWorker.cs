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
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Configuration;
using Nethermind.HonestValidator.Services;
using Nethermind.Logging.Microsoft;

namespace Nethermind.HonestValidator
{
    public class HonestValidatorWorker : BackgroundService
    {
        private readonly BeaconChainInformation _beaconChainInformation;
        private readonly IBeaconNodeApi _beaconNodeApi;
        private readonly IClientVersion _clientVersion;
        private readonly IClock _clock;
        private readonly DataDirectory _dataDirectory;
        private readonly IHostEnvironment _environment;
        private readonly ILogger _logger;
        private bool _stopped;
        private readonly ValidatorClient _validatorClient;

        public HonestValidatorWorker(ILogger<HonestValidatorWorker> logger,
            IClock clock,
            IHostEnvironment environment,
            DataDirectory dataDirectory,
            IBeaconNodeApi beaconNodeApi,
            BeaconChainInformation beaconChainInformation,
            ValidatorClient validatorClient,
            IClientVersion clientVersion)
        {
            _logger = logger;
            _clock = clock;
            _environment = environment;
            _dataDirectory = dataDirectory;
            _beaconNodeApi = beaconNodeApi;
            _beaconChainInformation = beaconChainInformation;
            _validatorClient = validatorClient;
            _clientVersion = clientVersion;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_logger.IsInfo())
                Log.HonestValidatorWorkerExecuteStarted(_logger, _clientVersion.Description,
                    _dataDirectory.ResolvedPath, _environment.EnvironmentName, Thread.CurrentThread.ManagedThreadId,
                    null);

            try
            {
                // Config
                // List of nodes
                // Validator private keys (or quickstart)
                // Seconds per slot

                string nodeVersion = string.Empty;
                while (nodeVersion == string.Empty)
                {
                    try
                    {
                        ApiResponse<string> nodeVersionResponse =
                            await _beaconNodeApi.GetNodeVersionAsync(stoppingToken).ConfigureAwait(false);
                        if (nodeVersionResponse.StatusCode == StatusCode.Success)
                        {
                            nodeVersion = nodeVersionResponse.Content;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WaitingForNodeVersion(_logger, ex);
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    }
                }

                ulong genesisTime = 0;
                while (genesisTime == 0)
                {
                    try
                    {
                        ApiResponse<ulong> genesisTimeResponse =
                            await _beaconNodeApi.GetGenesisTimeAsync(stoppingToken).ConfigureAwait(false);
                        if (genesisTimeResponse.StatusCode == StatusCode.Success)
                        {
                            genesisTime = genesisTimeResponse.Content;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WaitingForGenesisTime(_logger, ex);
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    }
                }

                Log.HonestValidatorWorkerConnected(_logger, nodeVersion, genesisTime, null);

                await _beaconChainInformation.SetGenesisTimeAsync(genesisTime).ConfigureAwait(false);

                while (!stoppingToken.IsCancellationRequested && !_stopped)
                {
                    try
                    {
                        DateTimeOffset clockTime = _clock.UtcNow();
                        ulong time = (ulong) clockTime.ToUnixTimeSeconds();

                        if (time > genesisTime)
                        {
                            await _validatorClient.OnTickAsync(_beaconChainInformation, time, stoppingToken)
                                .ConfigureAwait(false);
                        }

                        // Wait for remaining time, if any
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

            if (_logger.IsDebug())
                LogDebug.HonestValidatorWorkerExecuteExiting(_logger, Thread.CurrentThread.ManagedThreadId, null);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.HonestValidatorWorkerStarting(_logger, null);
            await base.StartAsync(cancellationToken);
            if (_logger.IsDebug()) LogDebug.HonestValidatorWorkerStarted(_logger, null);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.HonestValidatorWorkerStopping(_logger, null);
            _stopped = true;
            await base.StopAsync(cancellationToken);
        }
    }
}