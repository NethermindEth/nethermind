/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */


using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nethermind.Monitoring.Metrics
{
    public class MetricsHostedService : IHostedService, IDisposable
    {
        private const int IntervalSeconds = 5;
        private readonly IMetricsUpdater _metricsUpdater;
        private Timer _timer;

        public MetricsHostedService(IMetricsUpdater metricsUpdater)
        {
            _metricsUpdater = metricsUpdater;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _timer = new Timer(UpdateMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(IntervalSeconds));

            return Task.CompletedTask;
        }

        private void UpdateMetrics(object state)
        {
            _metricsUpdater.StartUpdating();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}