// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Monitoring
{
    public interface IMonitoringService
    {
        void RegisterMetrics(Type type);
        Task StartAsync();
        Task StopAsync();
    }
}
