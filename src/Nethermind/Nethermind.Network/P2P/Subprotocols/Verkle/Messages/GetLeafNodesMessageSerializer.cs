// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Verkle.Messages;

public class GetLeafNodesMessageSerializer : IZeroMessageSerializer<GetLeafNodesMessage>
{
    public void Serialize(IByteBuffer byteBuffer, GetLeafNodesMessage message)
    {
        (int contentLength, int allPathsLength) = CalculateLengths(message);

        byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength), true);
        NettyRlpStream stream = new(byteBuffer);

        stream.StartSequence(contentLength);

        stream.Encode(message.RequestId);
        stream.Encode(message.RootHash.Bytes);

        if (message.Paths is null || message.Paths.Length == 0)
        {
            stream.EncodeNullObject();
        }
        else
        {
            stream.StartSequence(allPathsLength);
            for (int i = 0; i < message.Paths.Length; i++)
            {
                stream.Encode(message.Paths[i]);
            }
        }

        stream.Encode(message.Bytes);
    }

    public GetLeafNodesMessage Deserialize(IByteBuffer byteBuffer)
    {
        GetLeafNodesMessage message = new();
        NettyRlpStream stream = new(byteBuffer);

        stream.ReadSequenceLength();

        message.RequestId = stream.DecodeLong();
        message.RootHash = new Hash256(stream.DecodeByteArray());
        message.Paths = stream.DecodeArray(stream => stream.DecodeByteArray());
        message.Bytes = stream.DecodeLong();

        return message;
    }

    private (int contentLength, int allPathsLength) CalculateLengths(GetLeafNodesMessage message)
    {
        int contentLength = Rlp.LengthOf(message.RequestId);
        contentLength += Rlp.LengthOf(message.RootHash.Bytes);

        int allPathsLength = 0;

        if (message.Paths is null || message.Paths.Length == 0)
        {
            allPathsLength = 1;
        }
        else
        {
            for (int i = 0; i < message.Paths.Length; i++)
            {
                allPathsLength += Rlp.LengthOf(message.Paths[i]);
            }
        }

        contentLength += Rlp.LengthOfSequence(allPathsLength);

        contentLength += Rlp.LengthOf(message.Bytes);

        return (contentLength, allPathsLength);
    }
}
