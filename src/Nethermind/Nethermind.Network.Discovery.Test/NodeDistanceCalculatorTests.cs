// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.RoutingTable;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeDistanceCalculatorTests
    {
        [Test]
        public void Same_length_distance()
        {
            NodeDistanceCalculator nodeDistanceCalculator = new(new DiscoveryConfig());
            int distance = nodeDistanceCalculator.CalculateDistance(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 });
            Assert.AreEqual(232, distance);
        }

        [Test]
        public void Left_shorter_distance()
        {
            NodeDistanceCalculator nodeDistanceCalculator = new(new DiscoveryConfig());
            int distance = nodeDistanceCalculator.CalculateDistance(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 });
            Assert.AreEqual(240, distance);
        }

        [Test]
        public void Right_shorter_distance()
        {
            NodeDistanceCalculator nodeDistanceCalculator = new(new DiscoveryConfig());
            int distance = nodeDistanceCalculator.CalculateDistance(new byte[] { 1, 2, 3 }, new byte[] { 1, 2 });
            Assert.AreEqual(240, distance);
        }
    }
}
