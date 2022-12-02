// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class BlockBodiesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            BlockHeader header = Build.A.BlockHeader.TestObject;
            Address to = Build.An.Address.FromNumber(1).TestObject;
            Transaction tx = Build.A.Transaction.WithTo(to).SignedAndResolved(new EthereumEcdsa(ChainId.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            tx.SenderAddress = null;
            BlockBodiesMessage message = new();
            message.Bodies = new[] { new BlockBody(new[] { tx }, new[] { header }) };

            var serializer = new BlockBodiesMessageSerializer();
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip_with_nulls()
        {
            BlockBodiesMessage message = new() { Bodies = new BlockBody[1] { null } };
            var serializer = new BlockBodiesMessageSerializer();
            SerializerTester.TestZero(serializer, message);
        }
    }
}
