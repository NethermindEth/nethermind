// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class TrieNodesMessageSerializer : IZeroMessageSerializer<TrieNodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, TrieNodesMessage message)
        {
            int nodesLength = message.Nodes?.RlpContentLength ?? 0;
            int nodesSeqLength = Rlp.LengthOfSequence(nodesLength);
            int contentLength = nodesSeqLength + Rlp.LengthOf(message.RequestId);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));

            NettyRlpStream rlpStream = new(byteBuffer);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.StartSequence(nodesLength);
            if (nodesLength > 0)
            {
                rlpStream.Write(message.Nodes!.RlpContentSpan);
            }
        }

        public TrieNodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);

            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startingPosition = ctx.Position;

            ctx.ReadSequenceLength();
            long requestId = ctx.DecodeLong();

            int prefixStart = ctx.Position;
            int innerLength = ctx.ReadSequenceLength();
            int totalLength = (ctx.Position - prefixStart) + innerLength;

            RlpByteArrayList list = new(memoryOwner, memoryOwner.Memory.Slice(prefixStart, totalLength));
            ctx.Position = prefixStart + totalLength;
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startingPosition));

            return new TrieNodesMessage(list) { RequestId = requestId };
        }
    }
}
