// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class GetBlockAccessListsMessageSerializer66(IZeroInnerMessageSerializer<GetBlockAccessListsMessage> innerSerializer)
    : IZeroInnerMessageSerializer<GetBlockAccessListsMessage66>
{
    private readonly IZeroInnerMessageSerializer<GetBlockAccessListsMessage> _innerSerializer = innerSerializer;

    public void Serialize(IByteBuffer byteBuffer, GetBlockAccessListsMessage66 message)
    {
        int length = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(length);

        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        stream.Encode(message.RequestId);
        _innerSerializer.Serialize(byteBuffer, message.EthMessage);
    }

    public GetBlockAccessListsMessage66 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private GetBlockAccessListsMessage66 Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        ctx.ReadSequenceLength();
        long requestId = ctx.DecodeLong();
        GetBlockAccessListsMessage ethMessage = GetBlockAccessListsMessageSerializer.Deserialize(ref ctx);
        return new GetBlockAccessListsMessage66(requestId, ethMessage);
    }

    public int GetLength(GetBlockAccessListsMessage66 message, out int contentLength)
    {
        int innerLength = _innerSerializer.GetLength(message.EthMessage, out _);

        contentLength =
            Rlp.LengthOf(message.RequestId) +
            innerLength;

        return Rlp.LengthOfSequence(contentLength);
    }
}
