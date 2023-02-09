// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class TrieNodesMessageSerializer : IZeroMessageSerializer<TrieNodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, TrieNodesMessage message)
        {
            (int contentLength, int nodesLength) = GetLength(message);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength), true);

            NettyRlpStream rlpStream = new(byteBuffer);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.StartSequence(nodesLength);
            for (int i = 0; i < message.Nodes.Length; i++)
            {
                rlpStream.Encode(message.Nodes[i]);
            }
        }

        public TrieNodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);

            rlpStream.ReadSequenceLength();

            long requestId = rlpStream.DecodeLong();
            byte[][] result = rlpStream.DecodeArray(stream => stream.DecodeByteArray());
            return new TrieNodesMessage(result) { RequestId = requestId };
        }

        public (int contentLength, int nodesLength) GetLength(TrieNodesMessage message)
        {
            int nodesLength = 0;
            for (int i = 0; i < message.Nodes.Length; i++)
            {
                nodesLength += Rlp.LengthOf(message.Nodes[i]);
            }

            return (nodesLength + Rlp.LengthOf(message.RequestId), nodesLength);
        }
    }
}
