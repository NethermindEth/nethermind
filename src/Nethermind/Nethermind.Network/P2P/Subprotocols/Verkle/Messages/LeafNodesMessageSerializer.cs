// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Verkle.Messages;

public class LeafNodesMessageSerializer : IZeroMessageSerializer<LeafNodesMessage>
{
    public void Serialize(IByteBuffer byteBuffer, LeafNodesMessage message)
    {
        int contentLength = GetLength(message, out int nodesLength);

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

    public LeafNodesMessage Deserialize(IByteBuffer byteBuffer)
    {
        NettyRlpStream rlpStream = new(byteBuffer);

        rlpStream.ReadSequenceLength();

        long requestId = rlpStream.DecodeLong();
        byte[][] result = rlpStream.DecodeArray(stream => stream.DecodeByteArray());
        return new LeafNodesMessage(result) { RequestId = requestId };
    }

    public int GetLength(LeafNodesMessage message, out int nodesLength)
    {
        nodesLength = 0;
        for (int i = 0; i < message.Nodes.Length; i++)
        {
            nodesLength += Rlp.LengthOf(message.Nodes[i]);
        }

        return (nodesLength + Rlp.LengthOf(message.RequestId));
    }
}
