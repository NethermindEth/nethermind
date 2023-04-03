// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class BlockBodiesMessageTests
    {
        [Test]
        public void Ctor_with_nulls()
        {
            var message = new BlockBodiesMessage(new[] { Build.A.Block.TestObject, null, Build.A.Block.TestObject });
            Assert.AreEqual(3, message.Bodies.Length);
        }

        [Test]
        public void To_string()
        {
            BlockBodiesMessage newBlockMessage = new();
            _ = newBlockMessage.ToString();
        }
    }
}
