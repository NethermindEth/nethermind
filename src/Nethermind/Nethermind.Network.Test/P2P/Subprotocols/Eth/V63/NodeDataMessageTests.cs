// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class NodeDataMessageTests
    {
        [Test]
        public void Accepts_nulls_inside()
        {
            byte[][] data = { new byte[] { 1, 2, 3 }, null };
            NodeDataMessage message = new(data);
            Assert.That(message.Data, Is.SameAs(data));
        }

        [Test]
        public void Accepts_nulls_top_level()
        {
            NodeDataMessage message = new(null);
            Assert.That(message.Data.Length, Is.EqualTo(0));
        }

        [Test]
        public void Sets_values_from_constructor_argument()
        {
            byte[][] data = { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } };
            NodeDataMessage message = new(data);
            Assert.That(message.Data, Is.SameAs(data));
        }

        [Test]
        public void To_string()
        {
            NodeDataMessage statusMessage = new(new byte[][] { });
            _ = statusMessage.ToString();
        }
    }
}
