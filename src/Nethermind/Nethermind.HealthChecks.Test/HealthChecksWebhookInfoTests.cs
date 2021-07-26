using System.Net;
using System;
using System.Collections.Generic;
using Nethermind.JsonRpc;
using NSubstitute;
using NUnit.Framework;
using Nethermind.HealthChecks;

namespace Nethermind.HealthChecks.Test
{
    public class HealthChecksWebhookInfoTests
    {
        [TestCase(new byte[]{127, 64, 34, 0}, "description", "nodeName")]
        [TestCase(new byte[]{0, 0, 0, 0}, "", "")]
        [TestCase(new byte[]{127, 64, 34, 0}, "D_E S-C.R0IPTION", "n.o'd_e-N7a/m]e")]
        public void HealthChecksWebhookInfo_returns_expected_results(byte[] ip_bytes, string description, string nodeName)
        {
            IPAddress ip = new IPAddress(ip_bytes);
            HealthChecksWebhookInfo healthChecksWebhookInfo = new HealthChecksWebhookInfo(description, ip, nodeName);
            string hostname = Dns.GetHostName();

            string expected;
            expected = "`" + description + "`" + Environment.NewLine
                + "NodeName: `" + nodeName + "`" + Environment.NewLine
                + "Hostname: `" + hostname + "`" + Environment.NewLine
                + "IP (external): `" + ip_bytes[0] + "." + ip_bytes[1] + "." + ip_bytes[2] + "." + ip_bytes[3] + "`";;

            Assert.AreEqual(expected, healthChecksWebhookInfo.GetFullInfo());
        }
    }
}
