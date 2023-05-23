// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Rlpx;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PacketTests
    {
        [Test]
        public void Asggins_values_from_constructor()
        {
            byte[] data = { 3, 4, 5 };
            Packet packet = new("eth", 2, data);
            Assert.That(packet.Protocol, Is.EqualTo("eth"));
            Assert.That(packet.PacketType, Is.EqualTo(2));
            Assert.That(packet.Data, Is.EqualTo(data));
        }
    }
}
