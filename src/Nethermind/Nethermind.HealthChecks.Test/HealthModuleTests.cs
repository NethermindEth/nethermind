//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
            Assert.AreEqual(false, nodeStatus.Data.Healthy);
            Assert.AreEqual("Still syncing", nodeStatus.Data.Messages[0]);
        }
    }
}
