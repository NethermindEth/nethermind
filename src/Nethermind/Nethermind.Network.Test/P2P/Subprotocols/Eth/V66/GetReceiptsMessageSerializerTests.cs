// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture]
    public class GetReceiptsMessageSerializerTests
    {
        //test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void RoundTrip()
        {
            Keccak a = new("0x00000000000000000000000000000000000000000000000000000000deadc0de");
            Keccak b = new("0x00000000000000000000000000000000000000000000000000000000feedbeef");

            Keccak[] hashes = { a, b };
            var ethMessage = new Network.P2P.Subprotocols.Eth.V63.Messages.GetReceiptsMessage(hashes);

            GetReceiptsMessage message = new(1111, ethMessage);

            GetReceiptsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "f847820457f842a000000000000000000000000000000000000000000000000000000000deadc0dea000000000000000000000000000000000000000000000000000000000feedbeef");
        }
    }
}
