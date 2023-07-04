// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Monitoring
{
    public interface IMonitoringService
    {
        Task StartAsync();
        Task StopAsync();
        void AddMetricsUpdateAction(Action callback);
    }
}
