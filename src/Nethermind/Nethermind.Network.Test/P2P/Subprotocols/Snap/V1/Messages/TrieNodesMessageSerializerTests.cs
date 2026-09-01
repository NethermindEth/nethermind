// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.V1.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class TrieNodesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            using ArrayPoolList<byte[]> data = new(2) { new byte[] { 0xde, 0xad, 0xc0, 0xde }, new byte[] { 0xfe, 0xed } };

            TrieNodesMessage message = new(new ByteArrayListAdapter(data));

            TrieNodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void RoundtripWithCorrectLength()
        {
            using ArrayPoolList<byte[]> data = new(2) { new byte[] { 0xde, 0xad, 0xc0, 0xde }, new byte[] { 0xfe, 0xed } };

            TrieNodesMessage message = new(new ByteArrayListAdapter(data));
            message.RequestId = 1;
            TrieNodesMessageSerializer serializer = new();
            Assert.That(serializer.Serialize(message).ToHexString(), Is.EqualTo("ca01c884deadc0de82feed"));
        }

        private static IEnumerable<TestCaseData> NodesLimitCases() =>
            ByteArrayListLimitTester.BoundaryCases(SnapMessageLimits.TrieNodesRlpLimit);

        [TestCaseSource(nameof(NodesLimitCases))]
        public void Deserialize_EnforcesNodesCountLimit(int nodeCount, bool shouldThrow) =>
            ByteArrayListLimitTester.AssertLimitEnforced(
                new TrieNodesMessageSerializer(),
                static nodes => new TrieNodesMessage(nodes) { RequestId = 1 },
                static message => message.Nodes.Count,
                nodeCount,
                shouldThrow);
    }
}
