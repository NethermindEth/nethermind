/* Class to provide useful information in healh checks' webhook notifications */

using System.Net;
using System;

namespace Nethermind.HealthChecks
{
    public class HealthChecksWebhookInfo
    {

        private string _description;
        private string _ip;
        private string _hostname;
        private string _nodeName;

        public HealthChecksWebhookInfo(string description, IPAddress externalIp, string nodeName)
        {
            //description
            _description = description;

            //hostName
            _hostname = Dns.GetHostName();                        

            //IP
            _ip = externalIp.ToString();

            //nodeName
            _nodeName = nodeName;
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
