// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
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
                Healthy = true,
                Errors = new List<string>(),
                IsSyncing = true,
                Messages = new List<(string, string)>()
                {
                    {("Still syncing", "Syncing in progress")}
                }
            });
            IHealthRpcModule healthRpcModule = new HealthRpcModule(nodeHealthService);
            ResultWrapper<NodeStatusResult> nodeStatus = healthRpcModule.health_nodeStatus();
            Assert.That(nodeStatus.Data.Healthy, Is.EqualTo(true));
            Assert.That(nodeStatus.Data.Messages.First(), Is.EqualTo("Still syncing"));
            Assert.That(nodeStatus.Data.IsSyncing, Is.EqualTo(true));
            Assert.That(nodeStatus.Data.Errors, Is.Empty);
        }
    }
}
