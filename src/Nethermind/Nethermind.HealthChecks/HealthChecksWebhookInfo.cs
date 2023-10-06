/* Class to provide useful information in healh checks' webhook notifications */

using System.Net;
using System;
using Nethermind.Monitoring.Metrics;
using Nethermind.Monitoring.Config;
using Nethermind.Network;

namespace Nethermind.HealthChecks
{
    public class HealthChecksWebhookInfo
    {
        private readonly string _description;
        private readonly string _ip;
        private readonly string _hostname;
        private readonly string _nodeName;

        public HealthChecksWebhookInfo(string description, IIPResolver ipResolver, IMetricsConfig metricsConfig, string hostname)
        {
            _description = description;
            _hostname = hostname;
            IPAddress externalIp = ipResolver.ExternalIp;
            _ip = externalIp.ToString();
            _nodeName = metricsConfig.NodeName;
        }

        public string GetFullInfo()
        {
            string healthInformation = "`" + _description + "`" + Environment.NewLine
                + "NodeName: `" + _nodeName + "`" + Environment.NewLine
                + "Hostname: `" + _hostname + "`" + Environment.NewLine
                + "IP (external): `" + _ip + "`";
            return healthInformation;
        }
    }
}
