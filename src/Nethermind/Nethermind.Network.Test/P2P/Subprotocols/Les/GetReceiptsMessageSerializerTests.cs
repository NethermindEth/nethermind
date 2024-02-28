// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class GetReceiptsMessageSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            Hash256[] hashes = { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            var ethMessage = new Network.P2P.Subprotocols.Eth.V63.Messages.GetReceiptsMessage(hashes.ToPooledList());

            using GetReceiptsMessage getReceiptsMessage = new(ethMessage, 1);

            GetReceiptsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, getReceiptsMessage);
        }
    }
}
