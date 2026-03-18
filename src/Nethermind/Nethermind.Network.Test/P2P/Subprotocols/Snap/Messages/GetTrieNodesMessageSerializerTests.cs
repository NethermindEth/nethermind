// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using FluentAssertions;
using System.Linq;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;
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
                Paths = PathGroup.EncodeToRlpPathGroupList([]),
                Bytes = 10
            };
            GetTrieNodesMessageSerializer serializer = new();

            AssertByteRoundtrip(serializer, msg);
        }

        [Test]
        public void Roundtrip_OneAccountPath()
        {
            GetTrieNodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                RootHash = TestItem.KeccakA,
                Paths = PathGroup.EncodeToRlpPathGroupList([
                    new() { Group = [TestItem.RandomDataA] }
                ]),
                Bytes = 10
            };
            GetTrieNodesMessageSerializer serializer = new();

            AssertByteRoundtrip(serializer, msg);
        }

        [Test]
        public void Roundtrip_MultiplePaths()
        {
            GetTrieNodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                RootHash = TestItem.KeccakA,
                Paths = PathGroup.EncodeToRlpPathGroupList([
                    new() { Group = [TestItem.RandomDataA, TestItem.RandomDataB] },
                    new() { Group = [TestItem.RandomDataC] }
                ]),
                Bytes = 10
            };
            GetTrieNodesMessageSerializer serializer = new();

            AssertByteRoundtrip(serializer, msg);
        }

        [Test]
        public void Roundtrip_MultiplePaths02()
        {
            GetTrieNodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                RootHash = TestItem.KeccakA,
                Paths = PathGroup.EncodeToRlpPathGroupList([
                    new() { Group = [TestItem.RandomDataA, TestItem.RandomDataB, TestItem.RandomDataD] },
                    new() { Group = [TestItem.RandomDataC] },
                    new() { Group = [TestItem.RandomDataC, TestItem.RandomDataA, TestItem.RandomDataB, TestItem.RandomDataD] }
                ]),
                Bytes = 10
            };
            GetTrieNodesMessageSerializer serializer = new();

            AssertByteRoundtrip(serializer, msg);
        }

        [Test]
        public void NullPathGroup()
        {
            byte[] data =
            {
                241, 136, 39, 223, 247, 171, 36, 79, 205, 54, 160, 107, 55, 36, 164, 27, 140, 56, 180, 109, 77, 2,
                251, 162, 187, 32, 116, 196, 122, 80, 126, 177, 106, 154, 75, 151, 143, 145, 211, 46, 64, 111, 175,
                195, 192, 193, 0, 130, 19, 136
            };

            GetTrieNodesMessageSerializer serializer = new();

            using GetTrieNodesMessage? msg = serializer.Deserialize(data);
            byte[] recode = serializer.Serialize(msg);

            recode.Should().BeEquivalentTo(data);
        }

        [Test]
        public void Deserialize_Throws_On_TooMany_Path_Groups()
        {
            PathGroup[] groups = Enumerable.Range(0, SnapMessageLimits.MaxRequestPathGroups + 1).Select(static _ => new PathGroup { Group = [TestItem.RandomDataA] }).ToArray();
            AssertDeserializeThrows(PathGroup.EncodeToRlpPathGroupList(groups));
        }

        [Test]
        public void Deserialize_Throws_On_TooMany_Paths_Per_Group()
        {
            byte[][] paths = Enumerable.Range(0, SnapMessageLimits.MaxRequestPathsPerGroup + 1).Select(static _ => TestItem.RandomDataA).ToArray();
            AssertDeserializeThrows(PathGroup.EncodeToRlpPathGroupList([new() { Group = paths }]));
        }

        private static void AssertDeserializeThrows(RlpPathGroupList paths)
        {
            GetTrieNodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                RootHash = TestItem.KeccakA,
                Paths = paths,
                Bytes = 10
            };

            GetTrieNodesMessageSerializer serializer = new();
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 64);
            try
            {
                serializer.Serialize(buffer, msg);
                Assert.Throws<RlpLimitException>(() => serializer.Deserialize(buffer));
            }
            finally
            {
                buffer.Release();
                msg.Dispose();
            }
        }

        /// <summary>
        /// RlpItemList has a ReadOnlySpan indexer that FluentAssertions cannot reflect over,
        /// so we verify roundtrip via byte equality instead of SerializerTester.TestZero.
        /// </summary>
        private static void AssertByteRoundtrip(GetTrieNodesMessageSerializer serializer, GetTrieNodesMessage msg)
        {
            using GetTrieNodesMessage _ = msg;
            using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16).AsDisposable();
            using DisposableByteBuffer buffer2 = PooledByteBufferAllocator.Default.Buffer(1024 * 16).AsDisposable();

            serializer.Serialize(buffer, msg);
            byte[] firstBytes = new byte[buffer.ReadableBytes];
            buffer.GetBytes(buffer.ReaderIndex, firstBytes);

            using GetTrieNodesMessage deserialized = serializer.Deserialize(buffer);

            deserialized.RequestId.Should().Be(msg.RequestId);
            deserialized.RootHash.Should().Be(msg.RootHash);
            deserialized.Bytes.Should().Be(msg.Bytes);
            deserialized.Paths.Count.Should().Be(msg.Paths.Count);

            serializer.Serialize(buffer2, deserialized);
            byte[] secondBytes = new byte[buffer2.ReadableBytes];
            buffer2.GetBytes(buffer2.ReaderIndex, secondBytes);

            secondBytes.Should().BeEquivalentTo(firstBytes);
        }
    }
}
