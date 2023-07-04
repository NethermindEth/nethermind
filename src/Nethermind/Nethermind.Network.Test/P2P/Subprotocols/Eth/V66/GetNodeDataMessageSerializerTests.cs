// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture]
    public class GetNodeDataMessageSerializerTests
    {
        //test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void Roundtrip()
        {
            Keccak[] keys = { new("0x00000000000000000000000000000000000000000000000000000000deadc0de"), new("0x00000000000000000000000000000000000000000000000000000000feedbeef") };

            var ethMessage = new Network.P2P.Subprotocols.Eth.V63.Messages.GetNodeDataMessage(keys);

            GetNodeDataMessage message = new(1111, ethMessage);

            GetNodeDataMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "f847820457f842a000000000000000000000000000000000000000000000000000000000deadc0dea000000000000000000000000000000000000000000000000000000000feedbeef");
        }
    }
}
