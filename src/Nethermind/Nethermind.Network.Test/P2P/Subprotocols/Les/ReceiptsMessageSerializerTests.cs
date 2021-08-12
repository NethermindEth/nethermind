//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Network.P2P.Subprotocols.Les;
using NUnit.Framework;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class ReceiptsMessageSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            TxReceipt[][] data = { new[] {Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(0).TestObject}, new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject}, new[] { Build.A.Receipt.WithAllFieldsFilled.WithTxType(TxType.AccessList).TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject}};
            Network.P2P.Subprotocols.Eth.V63.ReceiptsMessage ethMessage = new Network.P2P.Subprotocols.Eth.V63.ReceiptsMessage(data);
            ReceiptsMessage receiptsMessage = new ReceiptsMessage(ethMessage, 1, 2000);

            ReceiptsMessageSerializer serializer = new ReceiptsMessageSerializer(RopstenSpecProvider.Instance);

            // Eth.ReceiptsMessageSerializer intentionally excludes fields when deserializing.
            // I think it's probably best to not copy the test logic checking for this here.
            byte[] bytes = serializer.Serialize(receiptsMessage);
            ReceiptsMessage deserialized = serializer.Deserialize(bytes);

            Assert.AreEqual(receiptsMessage.RequestId, deserialized.RequestId, "RequestId");
            Assert.AreEqual(receiptsMessage.BufferValue, deserialized.BufferValue, "BufferValue");
        }
    }
}
