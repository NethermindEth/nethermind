// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Monitoring.Config;

[ConfigCategory(Description = "Configuration of the metrics provided by a Nethermind node for both, the Prometheus and the dotnet-counters.")]
public interface IMetricsConfig : IConfig
{
    [ConfigItem(Description = "The ip at which to expose Prometheus metrics.", DefaultValue = "127.0.0.1")]
    string ExposeHost { get; }

    [ConfigItem(Description = "The port to expose Prometheus metrics at.", DefaultValue = "null")]
    int? ExposePort { get; }

    [ConfigItem(Description = "Whether to publish various metrics to Prometheus Pushgateway at a given interval.", DefaultValue = "false")]
    bool Enabled { get; }

    [ConfigItem(Description = "Whether to publish metrics using .NET diagnostics that can be collected with dotnet-counters.", DefaultValue = "false")]
    bool CountersEnabled { get; }

    [ConfigItem(Description = "The Prometheus Pushgateway instance URL.", DefaultValue = "")]
    string PushGatewayUrl { get; }

    [ConfigItem(DefaultValue = "5", Description = "The frequency of pushing metrics to Prometheus, in seconds.")]
    int IntervalSeconds { get; }

    [ConfigItem(Description = "The name to display on the Grafana dashboard.", DefaultValue = "\"Nethermind\"")]
    string NodeName { get; }

    [ConfigItem(Description = "Whether to publish database size metrics.", DefaultValue = "true")]
    bool EnableDbSizeMetrics { get; }
}
