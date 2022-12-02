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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nethermind.Config;
using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
using Nethermind.Logging;

namespace Nethermind.HealthChecks
{
    public class FreeDiskSpaceChecker : IHostedService, IAsyncDisposable
    {
        private readonly IHealthChecksConfig _healthChecksConfig;
        private readonly ILogger _logger;
        private readonly IAvailableSpaceGetter _availableSpaceGetter;
        private readonly ITimer _timer;
        private static readonly int CheckPeriodMinutes = 1;

        public FreeDiskSpaceChecker(IHealthChecksConfig healthChecksConfig, ILogger logger, IAvailableSpaceGetter availableSpaceGetter, ITimerFactory timerFactory)
        {
            _healthChecksConfig = healthChecksConfig;
            _logger = logger;
            _availableSpaceGetter = availableSpaceGetter;
            _timer = timerFactory.CreateTimer(TimeSpan.FromMinutes(CheckPeriodMinutes));
            _timer.Elapsed += CheckDiskSpace;
        }

        private void CheckDiskSpace(object sender, EventArgs e)
        {
            foreach ((long freeSpace, double freeSpacePcnt) in _availableSpaceGetter.GetAvailableSpace())
            {
                if (freeSpacePcnt < _healthChecksConfig.LowStorageSpaceShutdownThreshold)
                {
                    if (_logger.IsError)
                        _logger.Error($"Free disk space is below {_healthChecksConfig.LowStorageSpaceShutdownThreshold:0.00}% - shutting down...");
                    Environment.Exit(ExitCodes.LowDiskSpace);
                }
                if (freeSpacePcnt < _healthChecksConfig.LowStorageSpaceWarningThreshold)
                {
                    double freeSpaceGB = (double)freeSpace / 1.GiB();
                    if (_logger.IsWarn)
                        _logger.Warn($"Running out of free disk space - only {freeSpaceGB:F2} GB ({freeSpacePcnt:F2}%) left!");
                }
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _timer.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer.Stop();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(default);
            _timer.Dispose();
        }
    }
}
