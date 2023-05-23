// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Monitoring
{
    public class NullMonitoringService : IMonitoringService
    {
        private NullMonitoringService()
        {
        }

        public static IMonitoringService Instance { get; } = new NullMonitoringService();

        public Task StartAsync()
        {
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            return Task.CompletedTask;
        }

        public void AddMetricsUpdateAction(Action callback) { }
    }
}
