// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.State.Snap;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetTrieNodesMessageSerializerTests
    {
        [Test]
        public void Roundtrip_NoPaths()
        {
            GetTrieNodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                RootHash = TestItem.KeccakA,
                Paths = Array.Empty<PathGroup>(),
                Bytes = 10
            };
            GetTrieNodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_OneAccountPath()
        {
            GetTrieNodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                RootHash = TestItem.KeccakA,
                Paths = new PathGroup[]
                    {
                        new PathGroup(){Group = new []{TestItem.RandomDataA}}
                    },
                Bytes = 10
            };
            GetTrieNodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_MultiplePaths()
        {
            GetTrieNodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                RootHash = TestItem.KeccakA,
                Paths = new PathGroup[]
                    {
                        new PathGroup(){Group = new []{TestItem.RandomDataA, TestItem.RandomDataB}},
                        new PathGroup(){Group = new []{TestItem.RandomDataC}}
                    },
                Bytes = 10
            };
            GetTrieNodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_MultiplePaths02()
        {
            GetTrieNodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                RootHash = TestItem.KeccakA,
                Paths = new PathGroup[]
                    {
                        new PathGroup(){Group = new []{TestItem.RandomDataA, TestItem.RandomDataB, TestItem.RandomDataD}},
                        new PathGroup(){Group = new []{TestItem.RandomDataC}},
                        new PathGroup(){Group = new []{TestItem.RandomDataC, TestItem.RandomDataA, TestItem.RandomDataB, TestItem.RandomDataD}}
                    },
                Bytes = 10
            };
            GetTrieNodesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void TestNullPathGroup()
        {
            byte[] data =
            {
                241, 136, 39, 223, 247, 171, 36, 79, 205, 54, 160, 107, 55, 36, 164, 27, 140, 56, 180, 109, 77, 2,
                251, 162, 187, 32, 116, 196, 122, 80, 126, 177, 106, 154, 75, 151, 143, 145, 211, 46, 64, 111, 175,
                195, 192, 193, 0, 130, 19, 136
            };

            GetTrieNodesMessageSerializer serializer = new();

            var msg = serializer.Deserialize(data);
            byte[] recode = serializer.Serialize(msg);

            recode.Should().BeEquivalentTo(data);
        }
    }
}
