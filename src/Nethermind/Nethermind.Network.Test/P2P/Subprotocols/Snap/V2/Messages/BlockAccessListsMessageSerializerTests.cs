// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.V2.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class BlockAccessListsMessageSerializerTests
    {
        private static readonly BlockAccessListsMessageSerializer Serializer = new();

        [Test]
        public void Roundtrip()
        {
            ArrayPoolList<byte[]> data = new(2)
            {
                new byte[] { 0xc4, 0x81, 0xaa, 0x81, 0xbb },
                new byte[] { 0xc2, 0x81, 0xcc },
            };

            using BlockAccessListsMessage message = new(new ByteArrayListAdapter(data)) { RequestId = 1 };

            SerializerTester.TestZero(Serializer, message);
        }

        [Test]
        public void Encodes_geth_wire_format_with_positional_miss()
        {
            ArrayPoolList<byte[]> data = new(3)
            {
                new byte[] { 0xc4, 0x81, 0xaa, 0x81, 0xbb },
                Array.Empty<byte>(),
                new byte[] { 0xc2, 0x81, 0xcc },
            };

            using BlockAccessListsMessage message = new(new ByteArrayListAdapter(data)) { RequestId = 42 };

            byte[] serialized = Serializer.Serialize(message);
            Assert.That(serialized, Is.EqualTo(Bytes.FromHexString("cb2ac9c481aa81bb80c281cc")));

            using BlockAccessListsMessage decoded = Serializer.Deserialize(serialized);
            Assert.That(decoded.RequestId, Is.EqualTo(42));
            Assert.That(decoded.BlockAccessLists.Count, Is.EqualTo(3));
            Assert.That(decoded.BlockAccessLists[0].ToArray(), Is.EqualTo(new byte[] { 0xc4, 0x81, 0xaa, 0x81, 0xbb }));
            Assert.That(decoded.BlockAccessLists[1].Length, Is.EqualTo(0));
            Assert.That(decoded.BlockAccessLists[2].ToArray(), Is.EqualTo(new byte[] { 0xc2, 0x81, 0xcc }));
        }
    }
}
