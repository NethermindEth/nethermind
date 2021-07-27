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
        private string _description;
        private string _ip;
        private string _hostname;
        private string _nodeName;

        public HealthChecksWebhookInfo(string description, IIPResolver ipResolver, IMetricsConfig metricsConfig, string hostname)
        {
            //description
            _description = description;

            //hostName
            _hostname = hostname;                        

            //IP
            IPAddress externalIp = ipResolver.ExternalIp;
            _ip = externalIp.ToString();

            //nodeName
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
