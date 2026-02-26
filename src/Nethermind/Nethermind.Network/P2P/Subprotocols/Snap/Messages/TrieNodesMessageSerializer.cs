// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class TrieNodesMessageSerializer : IZeroMessageSerializer<TrieNodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, TrieNodesMessage message)
        {
            (int contentLength, int nodesLength) = GetLength(message);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));

            NettyRlpStream rlpStream = new(byteBuffer);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.StartSequence(nodesLength);
            for (int i = 0; i < message.Nodes.Count; i++)
            {
                rlpStream.Encode(message.Nodes[i]);
            }
        }

        public TrieNodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);

            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startingPosition = ctx.Position;

            ctx.ReadSequenceLength();
            long requestId = ctx.DecodeLong();

            int innerLength = ctx.ReadSequenceLength();
            int dataStart = ctx.Position;

            RlpByteArrayList list = new(memoryOwner, memoryOwner.Memory.Slice(dataStart, innerLength));
            ctx.Position = dataStart + innerLength;
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startingPosition));

            return new TrieNodesMessage(list) { RequestId = requestId };
        }

        public static (int contentLength, int nodesLength) GetLength(TrieNodesMessage message)
        {
            int nodesLength = 0;
            for (int i = 0; i < message.Nodes.Count; i++)
            {
                nodesLength += Rlp.LengthOf(message.Nodes[i]);
            }

            return (Rlp.LengthOfSequence(nodesLength) + Rlp.LengthOf(message.RequestId), nodesLength);
        }
    }
}
