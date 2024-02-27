// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
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
            ArrayPoolList<byte[]> data = new(2) { new byte[] { 1, 2, 3 }, null };
            using NodeDataMessage message = new(data);
            Assert.That(message.Data, Is.SameAs(data));
        }

        [Test]
        public void Accepts_nulls_top_level()
        {
            using NodeDataMessage message = new(null);
            Assert.That(message.Data.Count, Is.EqualTo(0));
        }

        [Test]
        public void Sets_values_from_constructor_argument()
        {
            ArrayPoolList<byte[]> data = new(2) { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } };
            using NodeDataMessage message = new(data);
            Assert.That(message.Data, Is.SameAs(data));
        }

        [Test]
        public void To_string()
        {
            using NodeDataMessage statusMessage = new(ArrayPoolList<byte[]>.Empty());
            _ = statusMessage.ToString();
        }
    }
}
