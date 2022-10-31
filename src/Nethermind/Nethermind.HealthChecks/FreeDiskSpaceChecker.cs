//  Copyright (c) 2022 Demerzel Solutions Limited
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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nethermind.Api;
using Nethermind.Core.MessageBus;
using Nethermind.Logging;

namespace Nethermind.HealthChecks
{
    public readonly struct LowDiskSpaceMessage : IMessage
    {
        public LowDiskSpaceMessage(long freeDiskSpace)
        {
            AvailableDiskSpce = freeDiskSpace;
        }
        public long AvailableDiskSpce { get; init; }
    }

    public class FreeDiskSpaceChecker : IHostedService, IAsyncDisposable
    {
        private readonly ISimpleMessageBus _messageBus;
        private readonly IHealthChecksConfig _healthChecksConfig;
        private readonly IInitConfig _initConfig;
        private readonly ILogger _logger;
        private readonly IAvailableSpaceGetter _availableSpaceGetter;
        private readonly PeriodicTimer _timer;
        private Task _timerTask;
        public static readonly int BytesToGB = 1024 << 20;
        private static readonly int CheckPeriodMinutes = 5;

        public FreeDiskSpaceChecker(ISimpleMessageBus messageBus, IInitConfig initConfig, IHealthChecksConfig healthChecksConfig, ILogger logger, IAvailableSpaceGetter availableSpaceGetter)
        {
            _messageBus = messageBus;
            _healthChecksConfig = healthChecksConfig;
            _initConfig = initConfig;
            _logger = logger;
            _availableSpaceGetter = availableSpaceGetter;

            _timer = new PeriodicTimer(TimeSpan.FromMinutes(CheckPeriodMinutes));
        }

        private async Task CheckDiskSpace(CancellationToken cancellationToken)
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(cancellationToken))
                {
                    (long freeSpace, double freeSpacePcnt) = _availableSpaceGetter.GetAvailableSpace(_initConfig.BaseDbPath);
                    if (freeSpacePcnt < _healthChecksConfig.LowStorageSpaceShutdownThreshold)
                    {
                        if (_logger.IsError)
                            _logger.Error($"Free disk space is below {_healthChecksConfig.LowStorageSpaceShutdownThreshold:0.00}% - shutting down...");
                        await _messageBus.Publish(new LowDiskSpaceMessage(freeSpace));
                        _timer.Dispose();
                        break;
                    }
                    if (freeSpacePcnt < _healthChecksConfig.LowStorageSpaceWarningThreshold)
                    {
                        double freeSpaceGB = (double)freeSpace / BytesToGB;
                        if (_logger.IsWarn)
                            _logger.Warn($"Running out of free disk space - only {freeSpaceGB:F2} GB ({freeSpacePcnt:F2}%) left!");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException ex)
            {
                if (_logger.IsError)
                    _logger.Error($"Failed to monitor free disk space", ex);
                _timer.Dispose();
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return _timerTask = CheckDiskSpace(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_timerTask == null)
                return;
            _timer.Dispose();
            await _timerTask;
            _timerTask = null;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(default);
        }
    }
}
