// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class TrieNodesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            RlpByteArrayList data = BuildList(new byte[] { 0xde, 0xad, 0xc0, 0xde }, new byte[] { 0xfe, 0xed });

            TrieNodesMessage message = new(data);
            TrieNodesMessageSerializer serializer = new();

            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024);
            try
            {
                serializer.Serialize(buffer, message);
                using TrieNodesMessage deserialized = serializer.Deserialize(buffer);

                deserialized.Nodes.Should().NotBeNull();
                deserialized.Nodes!.Count.Should().Be(message.Nodes!.Count);
                for (int i = 0; i < message.Nodes.Count; i++)
                {
                    deserialized.Nodes[i].ToArray().Should().BeEquivalentTo(message.Nodes[i].ToArray());
                }
            }
            finally
            {
                buffer.Release();
                message.Dispose();
            }
        }

        [Test]
        public void RoundtripWithCorrectLength()
        {
            using RlpByteArrayList data = BuildList(new byte[] { 0xde, 0xad, 0xc0, 0xde }, new byte[] { 0xfe, 0xed });

            TrieNodesMessage message = new(data);
            message.RequestId = 1;
            TrieNodesMessageSerializer serializer = new();
            serializer.Serialize(message).ToHexString().Should().Be("ca01c884deadc0de82feed");
        }

        private static RlpByteArrayList BuildList(params byte[][] items)
        {
            int contentLength = 0;
            foreach (byte[] item in items) contentLength += Rlp.LengthOf(item);

            int totalLength = Rlp.LengthOfSequence(contentLength);
            byte[] buffer = new byte[totalLength];
            RlpStream stream = new(buffer);
            stream.StartSequence(contentLength);
            foreach (byte[] item in items) stream.Encode(item);

            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(totalLength);
            ((ReadOnlySpan<byte>)buffer).CopyTo(owner.Memory.Span);
            return new RlpByteArrayList(owner, owner.Memory[..totalLength]);
        }
    }
}
