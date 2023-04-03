/// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Les;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class GetContractCodesMessageSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            CodeRequest[] requests = new CodeRequest[]
            {
                new(TestItem.KeccakA, TestItem.KeccakB),
                new(TestItem.KeccakC, TestItem.KeccakD),
            };

            GetContractCodesMessage message = new(requests, 774);

            GetContractCodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
