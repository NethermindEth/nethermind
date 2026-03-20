// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;
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
            Hash256 a = new("0x00000000000000000000000000000000000000000000000000000000deadc0de");
            Hash256 b = new("0x00000000000000000000000000000000000000000000000000000000feedbeef");

            Hash256[] hashes = { a, b };
            using var ethMessage = new Network.P2P.Subprotocols.Eth.V63.Messages.GetReceiptsMessage(hashes.ToPooledList());

            GetReceiptsMessage message = new(1111, ethMessage);

            GetReceiptsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "f847820457f842a000000000000000000000000000000000000000000000000000000000deadc0dea000000000000000000000000000000000000000000000000000000000feedbeef");
        }

        [Test]
        public void RoundTrip_negative_request_id()
        {
            using var ethMessage = new Network.P2P.Subprotocols.Eth.V63.Messages.GetReceiptsMessage(new[] { Keccak.Zero }.ToPooledList());
            using GetReceiptsMessage message = new(-1, ethMessage);
            GetReceiptsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Rejects_request_id_longer_than_8_bytes()
        {
            GetReceiptsMessageSerializer serializer = new();
            IByteBuffer payload = Unpooled.WrappedBuffer([0xcb, 0x89, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0xc0]);

            Assert.Throws<RlpException>(() => serializer.Deserialize(payload));
        }

        [Test]
        public void Rejects_extra_outer_payload()
        {
            GetReceiptsMessageSerializer serializer = new();
            IByteBuffer payload = Unpooled.WrappedBuffer([0xc3, 0x01, 0xc0, 0x80]);

            Assert.Throws<RlpException>(() => serializer.Deserialize(payload));
        }
    }
}
