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
            int nodesLength = Rlp.LengthOfByteArrayList(message.Nodes);
            int contentLength = Rlp.LengthOf(message.RequestId) + nodesLength;
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
            NettyRlpStream rlpStream = new(byteBuffer);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.WriteByteArrayList(message.Nodes);
        }

        public TrieNodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startPos = ctx.Position;

            ctx.ReadSequenceLength();
            long requestId = ctx.DecodeLong();

            RlpByteArrayList list = RlpByteArrayList.DecodeList(ref ctx, memoryOwner);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startPos));

            return new TrieNodesMessage(list) { RequestId = requestId };
        }
    }
}
