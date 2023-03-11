// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Monitoring.Config
{
    public class MetricsConfig : IMetricsConfig
    {
        public int? ExposePort { get; set; } = null;
        public bool Enabled { get; set; } = false;
        public string PushGatewayUrl { get; set; } = "";
        public int IntervalSeconds { get; set; } = 5;
        public string NodeName { get; set; } = "Nethermind";
    }
}
