// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
            Assert.AreEqual(true, nodeStatus.Data.Healthy);
            Assert.AreEqual(true, nodeStatus.Data.IsSyncing);
            Assert.AreEqual(0, nodeStatus.Data.Errors.Count());
            Assert.AreEqual("Still syncing", nodeStatus.Data.Messages.First());
        }
    }
}
