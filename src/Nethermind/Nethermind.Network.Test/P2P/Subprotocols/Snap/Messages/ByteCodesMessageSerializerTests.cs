// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class ByteCodesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            ArrayPoolList<byte[]> data = new(2) { new byte[] { 0xde, 0xad, 0xc0, 0xde }, new byte[] { 0xfe, 0xed } };

            ByteCodesMessage message = new(data);

            ByteCodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void DecodeEncodeDecodeEmpty()
        {
            byte[] data = { 202, 136, 23, 106, 21, 106, 229, 131, 72, 176, 192 };
            ByteCodesMessageSerializer serializer = new();
            ByteCodesMessage decode = serializer.Deserialize(data);
            byte[] messageEncode = serializer.Serialize(decode);
            messageEncode.Should().BeEquivalentTo(data);
        }
    }
}
