// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.JsonRpc;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.HealthChecks.Test
{
    public class HealthModuleTests
    {
        [Test]
        public void NodeStatus_returns_expected_results()
        {
            INodeHealthService nodeHealthService = Substitute.For<INodeHealthService>();
            nodeHealthService.CheckHealth().Returns(new CheckHealthResult()
            {
                Healthy = false,
                Messages = new List<(string, string)>()
                {
                    {("Still syncing", "Syncing in progress")}
                }
            });
            IHealthRpcModule healthRpcModule = new HealthRpcModule(nodeHealthService);
            ResultWrapper<NodeStatusResult> nodeStatus = healthRpcModule.health_nodeStatus();
            Assert.That(nodeStatus.Data.Healthy, Is.EqualTo(false));
            Assert.That(nodeStatus.Data.Messages[0], Is.EqualTo("Still syncing"));
        }
    }
}
