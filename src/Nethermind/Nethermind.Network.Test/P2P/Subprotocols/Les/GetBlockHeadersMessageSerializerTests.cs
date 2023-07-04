// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class GetBlockHeadersMessageSerializerTests
    {
        [Test]
        public void RoundTripWithHash()
        {
            var ethMessage = new Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockHeadersMessage();
            ethMessage.StartBlockHash = Keccak.Compute("1");
            ethMessage.MaxHeaders = 10;
            ethMessage.Skip = 2;
            ethMessage.Reverse = 0;

            var message = new GetBlockHeadersMessage(ethMessage, 2);

            GetBlockHeadersMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "e602e4a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc60a0280");
        }

        [Test]
        public void RoundTripWithNumber()
        {
            var ethMessage = new Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockHeadersMessage();
            ethMessage.StartBlockNumber = 1;
            ethMessage.MaxHeaders = 10;
            ethMessage.Skip = 2;
            ethMessage.Reverse = 0;

            var message = new GetBlockHeadersMessage(ethMessage, 2);

            GetBlockHeadersMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "c602c4010a0280");
        }
    }
}
