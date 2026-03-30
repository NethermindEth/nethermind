// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class GetReceiptsMessageSerializer70(IZeroInnerMessageSerializer<GetReceiptsMessage> innerSerializer)
    : IZeroInnerMessageSerializer<GetReceiptsMessage70>
{
    private readonly IZeroInnerMessageSerializer<GetReceiptsMessage> _innerSerializer = innerSerializer;

    public void Serialize(IByteBuffer byteBuffer, GetReceiptsMessage70 message)
    {
        int length = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(length);

        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        stream.Encode(message.RequestId);
        stream.Encode(message.FirstBlockReceiptIndex);
        _innerSerializer.Serialize(byteBuffer, message.EthMessage);
    }

    public GetReceiptsMessage70 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private static GetReceiptsMessage70 Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        ctx.ReadSequenceLength();

        long requestId = ctx.DecodeLong();
        long firstIndex = ctx.DecodeLong();

        if (firstIndex < 0)
        {
            throw new RlpException("Negative firstBlockReceiptIndex is invalid");
        }

        GetReceiptsMessage ethMessage = GetReceiptsMessageSerializer.Deserialize(ref ctx);
        return new GetReceiptsMessage70(requestId, firstIndex, ethMessage);
    }

    public int GetLength(GetReceiptsMessage70 message, out int contentLength)
    {
        int innerLength = _innerSerializer.GetLength(message.EthMessage, out _);

        contentLength =
            Rlp.LengthOf(message.RequestId) +
            Rlp.LengthOf(message.FirstBlockReceiptIndex) +
            innerLength;

        return Rlp.LengthOfSequence(contentLength);
    }
}
