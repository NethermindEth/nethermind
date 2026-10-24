// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Monitoring;

namespace Nethermind.Monitoring;

public class MonitoringPlugin(IMetricsConfig metricsConfig) : INethermindPlugin
{
    public string Name => "Monitoring";
    public string Description => "Prometheus and dotnet-counters metrics";
    public string Author => "Nethermind";
    public bool Enabled => metricsConfig.IsMonitoringEnabled();
    public IModule Module => new MonitoringModule();
}
