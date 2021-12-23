using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Config
{
    public class NoCategoryConfig : INoCategoryConfig
    {
        public string Config { get; set; } = null;
        public string DataDir { get; set; }
        public string ConfigsDirectory { get; set; }
        public string BaseDbPath { get; set; }
        public string LogLevel { get; set; }
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
