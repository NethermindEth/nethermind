// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class ReceiptsMessageSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            TxReceipt[][] data = { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(0).TestObject }, new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject }, new[] { Build.A.Receipt.WithAllFieldsFilled.WithTxType(TxType.AccessList).TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject } };
            Network.P2P.Subprotocols.Eth.V63.Messages.ReceiptsMessage ethMessage = new(data);
            ReceiptsMessage receiptsMessage = new(ethMessage, 1, 2000);

            ReceiptsMessageSerializer serializer = new(MainnetSpecProvider.Instance);

            // Eth.ReceiptsMessageSerializer intentionally excludes fields when deserializing.
            // I think it's probably best to not copy the test logic checking for this here.
            byte[] bytes = serializer.Serialize(receiptsMessage);
            ReceiptsMessage deserialized = serializer.Deserialize(bytes);

            Assert.That(deserialized.RequestId, Is.EqualTo(receiptsMessage.RequestId), "RequestId");
            Assert.That(deserialized.BufferValue, Is.EqualTo(receiptsMessage.BufferValue), "BufferValue");
        }
    }
}
