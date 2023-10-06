// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class BlockHeadersMessageSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            var ethMessage = new Network.P2P.Subprotocols.Eth.V62.Messages.BlockHeadersMessage();
            ethMessage.BlockHeaders = new[] { Build.A.BlockHeader.TestObject };
            BlockHeadersMessage message = new(ethMessage, 2, 3000);

            BlockHeadersMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
