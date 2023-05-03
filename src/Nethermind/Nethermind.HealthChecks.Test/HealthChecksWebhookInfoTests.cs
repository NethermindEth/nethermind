// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System;
using System.Collections.Generic;
using Nethermind.JsonRpc;
using NSubstitute;
using NUnit.Framework;
using Nethermind.HealthChecks;
using Nethermind.Monitoring.Metrics;
using Nethermind.Monitoring.Config;
using Nethermind.Network;

namespace Nethermind.HealthChecks.Test
{
    public class HealthChecksWebhookInfoTests
    {
        [Test]
        public void HealthChecksWebhookInfo_returns_expected_results()
        {
            string description = "description";

            IIPResolver ipResolver = Substitute.For<IIPResolver>();
            byte[] ip = { 1, 2, 3, 4 };
            ipResolver.ExternalIp.Returns(new IPAddress(ip));

            IMetricsConfig metricsConfig = new MetricsConfig() { NodeName = "nodeName" };

            string hostname = "hostname";

            HealthChecksWebhookInfo healthChecksWebhookInfo = new HealthChecksWebhookInfo(description, ipResolver, metricsConfig, hostname);

            string expected = "`description`" + Environment.NewLine
                                              + "NodeName: `nodeName`" + Environment.NewLine
                                              + "Hostname: `hostname`" + Environment.NewLine
                                              + "IP (external): `1.2.3.4`";

            Assert.That(healthChecksWebhookInfo.GetFullInfo(), Is.EqualTo(expected));
        }
    }
}
