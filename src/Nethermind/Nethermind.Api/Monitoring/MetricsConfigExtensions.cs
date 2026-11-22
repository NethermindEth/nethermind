// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Api.Monitoring;

public static class MetricsConfigExtensions
{
    /// <summary>
    /// Whether any metrics consumer is enabled: Prometheus or dotnet-counters.
    /// </summary>
    public static bool IsMonitoringEnabled(this IMetricsConfig config) => config.Enabled || config.CountersEnabled;
}
