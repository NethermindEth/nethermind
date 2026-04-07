// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Monitoring;

public class NoopMonitoringService : IMonitoringService
{
    public static IMonitoringService Instance = new NoopMonitoringService();

    public Task StartAsync()
    {
        return Task.CompletedTask;
    }

    public void AddMetricsUpdateAction(Action callback)
    {
    }
}
