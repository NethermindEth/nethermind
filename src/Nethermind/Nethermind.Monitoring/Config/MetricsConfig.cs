// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Monitoring.Config
{
    public class MetricsConfig : IMetricsConfig
    {
        public int? ExposePort { get; set; } = null;
        public bool Enabled { get; set; } = false;
        public bool CountersEnabled { get; set; } = false;
        public string PushGatewayUrl { get; set; } = "";
        public int IntervalSeconds { get; set; } = 5;
        public string NodeName { get; set; } = "Nethermind";
        public bool EnableDbSizeMetrics { get; set; } = true;
    }
}
