// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class BlockAccessListsMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            ArrayPoolList<byte[]> data = new(2)
            {
                new byte[] { 0xc4, 0x81, 0xde, 0x81, 0xad },
                new byte[] { 0xc2, 0x81, 0xfe },
            };

            using BlockAccessListsMessage message = new(new ByteArrayListAdapter(data)) { RequestId = 1 };

            BlockAccessListsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Roundtrip_preserves_positional_empty_entries()
        {
            // EIP-8189: an unavailable block access list is returned as a positional empty entry, not skipped.
            ArrayPoolList<byte[]> data = new(3)
            {
                new byte[] { 0xc3, 1, 2, 3 },
                System.Array.Empty<byte>(),
                new byte[] { 0xc2, 4, 5 },
            };

            using BlockAccessListsMessage message = new(new ByteArrayListAdapter(data)) { RequestId = 7 };

            BlockAccessListsMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(message);
            using BlockAccessListsMessage decoded = serializer.Deserialize(serialized);

            Assert.That(decoded.RequestId, Is.EqualTo(7));
            Assert.That(decoded.BlockAccessLists.Count, Is.EqualTo(3));
            Assert.That(decoded.BlockAccessLists[0].ToArray(), Is.EqualTo(new byte[] { 0xc3, 1, 2, 3 }));
            Assert.That(decoded.BlockAccessLists[1].Length, Is.EqualTo(0));
            Assert.That(decoded.BlockAccessLists[2].ToArray(), Is.EqualTo(new byte[] { 0xc2, 4, 5 }));
        }
    }
}
