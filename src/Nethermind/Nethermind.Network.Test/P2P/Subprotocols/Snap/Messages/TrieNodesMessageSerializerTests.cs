// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class TrieNodesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            ArrayPoolList<byte[]> data = new () { new byte[] { 0xde, 0xad, 0xc0, 0xde }, new byte[] { 0xfe, 0xed } };

            TrieNodesMessage message = new(data);

            TrieNodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void RoundtripWithCorrectLength()
        {
            ArrayPoolList<byte[]> data = new () { new byte[] { 0xde, 0xad, 0xc0, 0xde }, new byte[] { 0xfe, 0xed } };

            TrieNodesMessage message = new(data);
            message.RequestId = 1;
            TrieNodesMessageSerializer serializer = new();
            serializer.Serialize(message).ToHexString().Should().Be("ca01c884deadc0de82feed");
        }
    }
}
