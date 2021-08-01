using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Config
{
    public interface INoCategoryConfig : IConfig
    {
        public string DataDir { get; set; }

        [ConfigItem(Description = "Path to the JSON configuration file.", DefaultValue = "null")]
        public string Config { get; set; }
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
