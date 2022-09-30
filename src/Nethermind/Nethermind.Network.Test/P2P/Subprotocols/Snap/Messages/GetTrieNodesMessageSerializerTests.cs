//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using FluentAssertions;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
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
                Paths = Array.Empty<PathGroup>(), //new MeasuredArray<MeasuredArray<byte[]>>(<MeasuredArray<byte[]>>()) ,
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
