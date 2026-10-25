// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Api.Monitoring;

public class MetricsConfig : IMetricsConfig
{
    public string ExposeHost { get; set; } = "+";
    public int? ExposePort { get; set; }
    public bool Enabled { get; set; } = false;
    public bool CountersEnabled { get; set; } = false;
    public string? PushGatewayUrl { get; set; }
    public string? PushGatewayUsername { get; set; }
    public string? PushGatewayPassword { get; set; }
    public int IntervalSeconds { get; set; } = 5;
    public int DbMetricIntervalSeconds { get; set; } = 60;
    public bool PauseDbMetricDuringBlockProcessing { get; set; } = true;
    public string NodeName { get; set; } = "Nethermind";
    public bool EnableDbSizeMetrics { get; set; } = true;
    public string MonitoringGroup { get; set; } = "nethermind";
    public string MonitoringJob { get; set; } = "nethermind";
    public bool EnableDetailedMetric { get; set; } = false;
    public bool InitializeStaticLabels { get; set; } = true;
}
