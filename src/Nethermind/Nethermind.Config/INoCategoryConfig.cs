using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Config
{
    public interface INoCategoryConfig : IConfig
    {
        [ConfigItem(Description = "Parent directory or path for BaseDbPath, KeyStoreDirectory, LogDirectory configurations.")]
        public string DataDir { get; set; }

        [ConfigItem(Description = "Path to the JSON configuration file.")]
        public string Config { get; set; }

        [ConfigItem(Description = "Path or directory for configuration files.", DefaultValue = "configs")]
        public string ConfigsDirectory { get; set; }

        [ConfigItem(Description = "Path or directory for database files.", DefaultValue = "db")]
        public string BaseDbPath { get; set; }

        [ConfigItem(Description = "Log level override. Possible values: OFF|TRACE|DEBUG|INFO|WARN|ERROR")]
        public string Log { get; set; }

        [ConfigItem(Description = "Path to the NLog config file")]
        public string LoggerConfigSource { get; set; }

        [ConfigItem(Description = "Plugins directory")]
        public string PluginsDirectory { get; set; }

        [ConfigItem(Description = "Sets the job name for metrics monitoring. Configurable via environment variable NETHERMIND_MONITORING_JOB", EnvironmentVariable = "NETHERMIND_MONITORING_JOB")]
        public string MonitoringJob { get; set; }

        [ConfigItem(Description = "Sets the default group name for metrics monitoring. Configurable via environment variable NETHERMIND_MONITORING_GROUP", EnvironmentVariable = "NETHERMIND_MONITORING_GROUP")]
        public string MonitoringGroup { get; set; }

        [ConfigItem(Description = "Sets the external IP for the node. Configurable via environment variable NETHERMIND_ENODE_IPADDRESS", EnvironmentVariable = "NETHERMIND_ENODE_IPADDRESS")]
        public string EnodeIpAddress { get; set; }

        [ConfigItem(Description = "Enables Hive plugin used for executing Hive Ethereum Tests. Configurable via environment variable NETHERMIND_HIVE_ENABLED", EnvironmentVariable = "NETHERMIND_HIVE_ENABLED")]
        public bool HiveEnabled { get; set; }

        [ConfigItem(Description = "Defines default URL for JSON RPC. Configurable via environment variable NETHERMIND_URL", EnvironmentVariable = "NETHERMIND_URL")]
        public string Url { get; set; }

        [ConfigItem(Description = "Defines CORS origins for JSON RPC. Configurable via environment variable NETHERMIND_CORS_ORIGINS", DefaultValue = "*", EnvironmentVariable = "NETHERMIND_CORS_ORIGINS")]
        public string CorsOrigins { get; set; }

        [ConfigItem(Description = "Defines host value for CLI function \"switchLocal\". Configurable via environment variable NETHERMIND_CLI_SWITCH_LOCAL", DefaultValue = "http://localhost", EnvironmentVariable = "NETHERMIND_CLI_SWITCH_LOCAL")]
        public string CliSwitchLocal { get; set; }
    }
}
