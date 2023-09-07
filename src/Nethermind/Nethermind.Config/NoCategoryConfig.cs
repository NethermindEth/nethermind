// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config
{
    public class NoCategoryConfig : INoCategoryConfig
    {
        public string Config { get; set; } = null;
        public string DataDir { get; set; }
        public string ConfigsDirectory { get; set; }
        public string BaseDbPath { get; set; }
        public string Log { get; set; }
        public string LoggerConfigSource { get; set; }
        public string PluginsDirectory { get; set; }
        public string MonitoringJob { get; set; }
        public string MonitoringGroup { get; set; }
        public string EnodeIpAddress { get; set; }
        public bool HiveEnabled { get; set; }
        public string Url { get; set; }
        public string CorsOrigins { get; set; }
        public string CliSwitchLocal { get; set; }
    }
}
