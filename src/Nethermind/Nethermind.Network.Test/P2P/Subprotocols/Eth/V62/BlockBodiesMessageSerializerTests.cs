// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            BlockBodiesMessageSerializer serializer = new();
            BlockHeader header = Build.A.BlockHeader.TestObject;
            BlockBodiesMessage message = new()
            {
                Bodies = new[] { new BlockBody(Build.A.BunchOfTransactions(), new[] { header }) }
            };

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip_with_nulls()
        {
            BlockBodiesMessage message = new() { Bodies = new BlockBody[1] { null } };
            BlockBodiesMessageSerializer serializer = new BlockBodiesMessageSerializer();
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip_with_withdrawals()
        {
            Address to = Build.An.Address.FromNumber(1).TestObject;
            Transaction tx = Build.A.Transaction.WithTo(to).SignedAndResolved(new EthereumEcdsa(NetworkId.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            tx.SenderAddress = null;
            BlockBodiesMessage message = new();
            message.Bodies = new[] { new BlockBody(new[] { tx }, Array.Empty<BlockHeader>(), Array.Empty<Withdrawal>()) };

            BlockBodiesMessageSerializer serializer = new BlockBodiesMessageSerializer();
            SerializerTester.TestZero(serializer, message);
        }
    }
}
