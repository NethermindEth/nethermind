/// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class BlockBodiesSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            BlockHeader header = Build.A.BlockHeader.TestObject;
            Address to = Build.An.Address.FromNumber(1).TestObject;
            Transaction tx = Build.A.Transaction.WithTo(to).SignedAndResolved(new EthereumEcdsa(RopstenSpecProvider.Instance.ChainId, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            tx.SenderAddress = null;
            var ethMessage = new Network.P2P.Subprotocols.Eth.V62.Messages.BlockBodiesMessage();
            ethMessage.Bodies = new[] { new BlockBody(new[] { tx }, new[] { header }) };

            BlockBodiesMessage message = new(ethMessage, 1, 1000);

            BlockBodiesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
