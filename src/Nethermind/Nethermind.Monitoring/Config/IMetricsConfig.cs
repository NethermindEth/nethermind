// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Monitoring.Config;

[ConfigCategory(Description = "Configuration of the metrics provided by a Nethermind node for both, the Prometheus and the dotnet-counters.")]
public interface IMetricsConfig : IConfig
{
    [ConfigItem(Description = "The IP address to expose Prometheus metrics at. The value of `+` means listening on all available hostnames. Setting this to `localhost` prevents remote access.", DefaultValue = "+")]
    string ExposeHost { get; }

    [ConfigItem(Description = "The port to expose Prometheus metrics at.")]
    int? ExposePort { get; }

    [ConfigItem(Description = "Whether to publish various metrics to Prometheus Pushgateway at a given interval.", DefaultValue = "false")]
    bool Enabled { get; }

    [ConfigItem(Description = "Whether to publish metrics using .NET diagnostics that can be collected with dotnet-counters.", DefaultValue = "false")]
    bool CountersEnabled { get; }

    [ConfigItem(Description = "The Prometheus Pushgateway instance URL.")]
    string PushGatewayUrl { get; }

    [ConfigItem(DefaultValue = "5", Description = "The frequency of pushing metrics to Prometheus, in seconds.")]
    int IntervalSeconds { get; }

    [ConfigItem(Description = "The name to display on the Grafana dashboard.", DefaultValue = "Nethermind")]
    string NodeName { get; }

    [ConfigItem(Description = "Whether to publish database size metrics.", DefaultValue = "true")]
    bool EnableDbSizeMetrics { get; }

    [ConfigItem(Description = "The Prometheus metrics group name.", DefaultValue = "nethermind")]
    string MonitoringGroup { get; }

    [ConfigItem(Description = "The Prometheus metrics job name.", DefaultValue = "nethermind")]
    string MonitoringJob { get; }

    [ConfigItem(Description = "Enable detailed metric", DefaultValue = "false")]
    bool EnableDetailedMetric { get; }

    [ConfigItem(Description = "Enable static label initialization", DefaultValue = "true", HiddenFromDocs = true)]
    bool InitializeStaticLabels { get; set; }
}
