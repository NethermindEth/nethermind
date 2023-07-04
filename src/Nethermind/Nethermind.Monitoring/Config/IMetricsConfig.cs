// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Monitoring.Config
{
    [ConfigCategory(Description = "Configuration of the metrics provided by a Nethermind node for both, the Prometheus and the dotnet-counters.")]
    public interface IMetricsConfig : IConfig
    {
        [ConfigItem(Description = "If set, the node exposes Prometheus metrics on the given port.", DefaultValue = "null")]
        int? ExposePort { get; }

        [ConfigItem(Description = "If 'true',the node publishes various metrics to Prometheus Pushgateway at given interval.", DefaultValue = "false")]
        bool Enabled { get; }

        [ConfigItem(Description = "If 'true',the node publishes metrics using .NET diagnostics that can be collected with dotnet-counters.", DefaultValue = "false")]
        bool CountersEnabled { get; }

        [ConfigItem(Description = "Prometheus Pushgateway URL.", DefaultValue = "")]
        string PushGatewayUrl { get; }

        [ConfigItem(DefaultValue = "5", Description = "Defines how often metrics are pushed to Prometheus")]
        int IntervalSeconds { get; }

        [ConfigItem(Description = "Name displayed in the Grafana dashboard", DefaultValue = "\"Nethermind\"")]
        string NodeName { get; }

        [ConfigItem(Description = "If set, will push db size metrics", DefaultValue = "true")]
        bool EnableDbSizeMetrics { get; }
    }
}
