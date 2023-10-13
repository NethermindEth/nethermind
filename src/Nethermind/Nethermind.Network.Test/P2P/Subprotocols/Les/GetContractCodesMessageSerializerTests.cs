/// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Les;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
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
                new(TestItem._commitmentA, TestItem._commitmentB),
                new(TestItem._commitmentC, TestItem._commitmentD),
            };

            GetContractCodesMessage message = new(requests, 774);

            GetContractCodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
