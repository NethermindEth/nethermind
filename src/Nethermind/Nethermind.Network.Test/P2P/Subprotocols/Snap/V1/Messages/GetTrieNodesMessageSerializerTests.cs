// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using System;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.State.Snap;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.V1.Messages
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
        public void Accepts_full_compact_path([Values] bool storage)
        {
            byte[] path = new byte[33];
            path[^1] = 0x42;
            PathGroup group = new() { Group = storage ? [TestItem.KeccakA.Bytes.ToArray(), path] : [path] };
            GetTrieNodesMessage message = new()
            {
                RequestId = 1,
                RootHash = TestItem.KeccakA,
                Paths = PathGroup.EncodeToRlpPathGroupList([group]),
                Bytes = 100
            };
            AssertByteRoundtrip(new GetTrieNodesMessageSerializer(), message);
        }

        /// <summary>
        /// snap/1 answers a well-formed path no trie node can sit at with an empty node rather than dropping the peer, so
        /// decoding keeps it for the server; one too long for any trie decodes to <see cref="PathGroup.TooLongPath"/>
        /// instead of a copy, and the message keeps its bytes.
        /// </summary>
        [TestCase(true, 33, 0, false)]
        [TestCase(false, 33, 0x10, false)]
        [TestCase(true, 34, 0, true)]
        [TestCase(false, 34, 0, true)]
        [TestCase(false, 55, 0, true)]
        [TestCase(false, 65, 0, true)]
        [TestCase(false, 1 << 20, 0, true)]
        public void Keeps_path_beyond_its_length_bound(bool accountKey, int length, byte prefix, bool tooLong)
        {
            byte[] path = new byte[length];
            path[0] = prefix;
            path[^1] = 0x42;
            PathGroup group = new() { Group = accountKey ? [path, []] : [path] };
            using GetTrieNodesMessage message = new()
            {
                RootHash = TestItem.KeccakA,
                Paths = PathGroup.EncodeToRlpPathGroupList([group])
            };
            GetTrieNodesMessageSerializer serializer = new();
            byte[] encoded = new byte[serializer.GetLength(message)];
            serializer.Serialize(encoded, message);

            using GetTrieNodesMessage deserialized = serializer.Deserialize(encoded);
            byte[][] decoded = deserialized.Paths.Single().Group;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded[0], tooLong ? Is.SameAs(PathGroup.TooLongPath) : Is.EqualTo(path));
                Assert.That(decoded[1..], Is.EqualTo(group.Group[1..]));
                Assert.That(serializer.SerializeToArray(deserialized), Is.EqualTo(encoded));
            }
        }

        [Test]
        public void Does_not_copy_path_too_long_for_any_trie()
        {
            GetTrieNodesMessageSerializer serializer = new();
            byte[] Encode(int length)
            {
                using GetTrieNodesMessage message = new()
                {
                    RootHash = TestItem.KeccakA,
                    Paths = PathGroup.EncodeToRlpPathGroupList([new PathGroup { Group = [new byte[length]] }])
                };
                return serializer.SerializeToArray(message);
            }

            // Decodes a group once first, so the measured one allocates nothing the first decode does.
            using (GetTrieNodesMessage warmUp = serializer.Deserialize(Encode(PathGroup.MaxPathLength + 1)))
                _ = warmUp.Paths[0];
            using GetTrieNodesMessage deserialized = serializer.Deserialize(Encode(1 << 20));

            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[][] decoded = deserialized.Paths[0].Group;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded, Has.Length.EqualTo(1));
                Assert.That(decoded[0], Is.SameAs(PathGroup.TooLongPath));
                Assert.That(allocated, Is.LessThan(4096), "the 1 MB path is not copied");
            }
        }

        [TestCase("e501a0{0}c1c501")] // a group item overruns its group
        [TestCase("e701a0{0}c3c2850001")] // a path overruns its group
        [TestCase("e701a0{0}c3c2c10101")] // a path is a list
        public void Rejects_malformed_path_group(string template)
        {
            byte[] encoded = Convert.FromHexString(string.Format(template, new string('0', 64)));

            Assert.That(() => new GetTrieNodesMessageSerializer().Deserialize(encoded).Dispose(), Throws.InstanceOf<RlpReaderException>());
        }

        [Test]
        public void Roundtrip_MultiplePaths()
        {
            GetTrieNodesMessage msg = new()
            {
                RequestId = 1,
                RootHash = TestItem.KeccakA,
                Paths = PathGroup.EncodeToRlpPathGroupList([
                    new() { Group = [TestItem.RandomDataA, TestItem.RandomDataB] },
                    new() { Group = [TestItem.RandomDataC] }
                ]),
                Bytes = 10
            };
            GetTrieNodesMessageSerializer serializer = new();

            AssertByteRoundtrip(serializer, msg, "f501a003783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760d1c9830102038404050607c685010208090a0a");
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
            byte[] recode = serializer.SerializeToArray(msg);

            Assert.That(recode, Is.EqualTo(data));
        }

        [Test]
        public void Deserialize_throws_on_null_root_hash()
        {
            using GetTrieNodesMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                RootHash = null!,
                Paths = PathGroup.EncodeToRlpPathGroupList([]),
                Bytes = 10
            };
            GetTrieNodesMessageSerializer serializer = new();
            using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024).AsDisposable();
            serializer.Serialize(buffer, msg);

            Assert.That(() => serializer.Deserialize(buffer), Throws.InstanceOf<RlpReaderException>());
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
        /// RlpItemList has a ReadOnlySpan indexer that structural assertions cannot reflect over,
        /// so we verify roundtrip via byte equality.
        /// </summary>
        private static void AssertByteRoundtrip(GetTrieNodesMessageSerializer serializer, GetTrieNodesMessage msg, string? expectedData = null)
        {
            using GetTrieNodesMessage _ = msg;
            using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16).AsDisposable();
            using DisposableByteBuffer buffer2 = PooledByteBufferAllocator.Default.Buffer(1024 * 16).AsDisposable();

            serializer.Serialize(buffer, msg);
            byte[] firstBytes = new byte[buffer.ReadableBytes];
            buffer.GetBytes(buffer.ReaderIndex, firstBytes);

            using GetTrieNodesMessage deserialized = serializer.Deserialize(buffer);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(deserialized.RequestId, Is.EqualTo(msg.RequestId));
                Assert.That(deserialized.RootHash, Is.EqualTo(msg.RootHash));
                Assert.That(deserialized.Bytes, Is.EqualTo(msg.Bytes));
                Assert.That(deserialized.Paths.Count, Is.EqualTo(msg.Paths.Count));
            }

            serializer.Serialize(buffer2, deserialized);
            byte[] secondBytes = new byte[buffer2.ReadableBytes];
            buffer2.GetBytes(buffer2.ReaderIndex, secondBytes);

            Assert.That(secondBytes, Is.EqualTo(firstBytes));
            if (expectedData is not null)
            {
                Assert.That(firstBytes.ToHexString(), Is.EqualTo(expectedData));
            }
        }
    }
}
