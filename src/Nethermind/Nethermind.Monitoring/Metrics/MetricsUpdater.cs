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

namespace Nethermind.Monitoring.Metrics
{
    public class MetricsUpdater : IMetricsUpdater
    {
        private readonly int _intervalSeconds;
        private Timer _timer;
        private readonly MetricsRegistry _metrics = new MetricsRegistry();

        public MetricsUpdater(int intervalSeconds = 5)
        {
            _intervalSeconds = intervalSeconds;
        }
        
        public void StartUpdating()
        {
            _timer = new Timer(UpdateMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(_intervalSeconds));
        }

        public void StopUpdating()
        {
            _timer?.Change(Timeout.Infinite, 0);
        }

        private void UpdateMetrics(object state)
        {
            _metrics.UpdateMetrics(typeof(Blockchain.Metrics));
            _metrics.UpdateMetrics(typeof(Evm.Metrics));
            _metrics.UpdateMetrics(typeof(Store.Metrics));
            _metrics.UpdateMetrics(typeof(Network.Metrics));
            _metrics.UpdateMetrics(typeof(JsonRpc.Metrics));
        }
    }
}