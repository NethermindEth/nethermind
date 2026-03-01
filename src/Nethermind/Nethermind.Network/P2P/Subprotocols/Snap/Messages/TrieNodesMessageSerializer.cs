// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
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
            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();

            ctx.ReadSequenceLength();

            long requestId = ctx.DecodeLong();
            IOwnedReadOnlyList<byte[]> result = ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext c) => c.DecodeByteArray());
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            return new TrieNodesMessage(result) { RequestId = requestId };
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
