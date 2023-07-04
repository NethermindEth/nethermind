// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture]
    public class NodeDataMessageSerializerTests
    {
        //test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void Roundtrip()
        {
            byte[][] data = { new byte[] { 0xde, 0xad, 0xc0, 0xde }, new byte[] { 0xfe, 0xed, 0xbe, 0xef } };
            var ethMessage = new Network.P2P.Subprotocols.Eth.V63.Messages.NodeDataMessage(data);

            NodeDataMessage message = new(1111, ethMessage);

            NodeDataMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "ce820457ca84deadc0de84feedbeef");
        }
    }
}
