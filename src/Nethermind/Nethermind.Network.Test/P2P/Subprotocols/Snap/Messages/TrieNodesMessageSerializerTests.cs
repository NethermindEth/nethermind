// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            byte[][] data = { new byte[] { 0xde, 0xad, 0xc0, 0xde }, new byte[] { 0xfe, 0xed } };

            TrieNodesMessage message = new(data);

            TrieNodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
