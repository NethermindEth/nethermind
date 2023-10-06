// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Timers;
using Nethermind.Logging;

namespace Nethermind.HealthChecks
{
    public class FreeDiskSpaceChecker : IHostedService, IAsyncDisposable
    {
        private readonly IHealthChecksConfig _healthChecksConfig;
        private readonly ILogger _logger;
        private readonly IDriveInfo[] _drives;
        private readonly IProcessExitSource _processExitSource;
        private readonly ITimer _timer;
        private readonly double _checkPeriodMinutes;

        public FreeDiskSpaceChecker(IHealthChecksConfig healthChecksConfig,
            IDriveInfo[] drives,
            ITimerFactory timerFactory,
            IProcessExitSource processExitSource,
            ILogger logger,
            double checkPeriodMinutes = 1)
        {
            _healthChecksConfig = healthChecksConfig;
            _logger = logger;
            _drives = drives;
            _processExitSource = processExitSource;
            _checkPeriodMinutes = checkPeriodMinutes;
            _timer = timerFactory.CreateTimer(TimeSpan.FromMinutes(_checkPeriodMinutes));
            _timer.Elapsed += CheckDiskSpace;
        }

        private void CheckDiskSpace(object sender, EventArgs e)
        {
            for (int index = 0; index < _drives.Length; index++)
            {
                IDriveInfo drive = _drives[index];
                double freeSpacePercent = drive.GetFreeSpacePercentage();
                if (freeSpacePercent < _healthChecksConfig.LowStorageSpaceShutdownThreshold)
                {
                    if (_logger.IsError) _logger.Error($"Free disk space in '{drive.RootDirectory.FullName}' is below {_healthChecksConfig.LowStorageSpaceShutdownThreshold:0.00}% - shutting down...");
                    _processExitSource.Exit(ExitCodes.LowDiskSpace);
                }

                if (freeSpacePercent < _healthChecksConfig.LowStorageSpaceWarningThreshold)
                {
                    if (_logger.IsWarn) _logger.Warn($"Running out of free disk space in '{drive.RootDirectory.FullName}' - only {drive.GetFreeSpaceInGiB():F2} GB ({freeSpacePercent:F2}%) left!");
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

        public void EnsureEnoughFreeSpaceOnStart(ITimerFactory timerFactory)
        {
            float minAvailableSpaceThreshold = 2 * _healthChecksConfig.LowStorageSpaceShutdownThreshold;
            if (_healthChecksConfig.LowStorageSpaceWarningThreshold > 0 && _healthChecksConfig.LowStorageSpaceWarningThreshold > _healthChecksConfig.LowStorageSpaceShutdownThreshold)
                minAvailableSpaceThreshold = _healthChecksConfig.LowStorageSpaceWarningThreshold / 2.0f;

            if (!IsEnoughDiskSpace(minAvailableSpaceThreshold))
            {
                if (_healthChecksConfig.LowStorageCheckAwaitOnStartup)
                {
                    ManualResetEventSlim mre = new(false);
                    using ITimer timer = timerFactory.CreateTimer(TimeSpan.FromMinutes(_checkPeriodMinutes));
                    timer.Elapsed += (t, e) =>
                    {
                        if (IsEnoughDiskSpace(minAvailableSpaceThreshold))
                            mre.Set();
                    };

                    timer.Start();
                    mre.Wait();
                }
                else
                {
                    _processExitSource.Exit(ExitCodes.LowDiskSpace);
                }
            }
        }

        private bool IsEnoughDiskSpace(float minAvailableSpaceThreshold)
        {
            bool enoughSpace = true;
            for (int index = 0; index < _drives.Length; index++)
            {
                IDriveInfo drive = _drives[index];
                double freeSpacePercent = drive.GetFreeSpacePercentage();
                enoughSpace &= freeSpacePercent >= minAvailableSpaceThreshold;
                if (freeSpacePercent < minAvailableSpaceThreshold)
                {
                    double minAvailableSpace = drive.GetFreeSpaceInGiB() / freeSpacePercent * minAvailableSpaceThreshold;
                    if (_logger.IsWarn)
                        _logger.Warn($"Not enough free disk space in '{drive.RootDirectory.FullName}' to safely run a node - please provide at least {minAvailableSpace:F2} GB to continue initialization.");
                }
            }
            return enoughSpace;
        }
    }
}
