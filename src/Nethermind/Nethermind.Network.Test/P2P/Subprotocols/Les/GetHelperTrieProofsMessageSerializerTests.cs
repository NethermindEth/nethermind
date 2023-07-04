// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Les;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class GetHelperTrieProofsMessageSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            HelperTrieRequest[] requests = new HelperTrieRequest[]
            {
                new(HelperTrieType.CHT, 177, TestItem.RandomDataA, 2, 1),
                new(HelperTrieType.BloomBits, 77, TestItem.RandomDataB, 4, 0),
            };
            GetHelperTrieProofsMessage message = new();
            message.RequestId = 100;
            message.Requests = requests;

            GetHelperTrieProofsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
