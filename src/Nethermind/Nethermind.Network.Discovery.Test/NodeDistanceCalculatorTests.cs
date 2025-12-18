// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
            int distance = nodeDistanceCalculator.CalculateDistance(new Hash256(new byte[] { 1, 2, 3 }.PadLeft(32)), new Hash256(new byte[] { 1, 2, 3 }.PadLeft(32)));
            Assert.That(distance, Is.EqualTo(0));
        }

        [Test]
        public void Left_shorter_distance()
        {
            NodeDistanceCalculator nodeDistanceCalculator = new(new DiscoveryConfig());
            int distance = nodeDistanceCalculator.CalculateDistance(new Hash256(new byte[] { 1, 2 }.PadLeft(32)), new Hash256(new byte[] { 1, 2, 3 }.PadLeft(32)));
            Assert.That(distance, Is.EqualTo(17));
        }

        [Test]
        public void Right_shorter_distance()
        {
            NodeDistanceCalculator nodeDistanceCalculator = new(new DiscoveryConfig());
            int distance = nodeDistanceCalculator.CalculateDistance(new Hash256(new byte[] { 1, 2, 3 }.PadLeft(32)), new Hash256(new byte[] { 1, 2 }.PadLeft(32)));
            Assert.That(distance, Is.EqualTo(17));
        }
    }
}
