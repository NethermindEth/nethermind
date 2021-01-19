//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Network.Config;
using Nethermind.Network.Discovery.RoutingTable;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeDistanceCalculatorTests
    {
        [Test]
        public void Same_length_distance()
        {
            NodeDistanceCalculator nodeDistanceCalculator = new NodeDistanceCalculator(new DiscoveryConfig());
            int distance = nodeDistanceCalculator.CalculateDistance(new byte[] {1, 2, 3}, new byte[] {1, 2, 3});
            Assert.AreEqual(232, distance);
        }
        
        [Test]
        public void Left_shorter_distance()
        {
            NodeDistanceCalculator nodeDistanceCalculator = new NodeDistanceCalculator(new DiscoveryConfig());
            int distance = nodeDistanceCalculator.CalculateDistance(new byte[] {1, 2}, new byte[] {1, 2, 3});
            Assert.AreEqual(240, distance);
        }
        
        [Test]
        public void Right_shorter_distance()
        {
            NodeDistanceCalculator nodeDistanceCalculator = new NodeDistanceCalculator(new DiscoveryConfig());
            int distance = nodeDistanceCalculator.CalculateDistance(new byte[] {1, 2, 3}, new byte[] {1, 2});
            Assert.AreEqual(240, distance);
        }
    }
}
