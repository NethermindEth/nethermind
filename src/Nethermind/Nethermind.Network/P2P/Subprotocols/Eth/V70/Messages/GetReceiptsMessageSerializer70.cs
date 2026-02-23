// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class GetReceiptsMessageSerializer70(IZeroInnerMessageSerializer<V63.Messages.GetReceiptsMessage> innerSerializer)
    : IZeroInnerMessageSerializer<GetReceiptsMessage70>
{
    private readonly IZeroInnerMessageSerializer<V63.Messages.GetReceiptsMessage> _innerSerializer = innerSerializer;

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

    public GetReceiptsMessage70 Deserialize(IByteBuffer byteBuffer)
    {
        NettyRlpStream stream = new(byteBuffer);
        stream.ReadSequenceLength();

        long requestId = stream.DecodeLong();
        long firstIndex = stream.DecodeLong();
        if (firstIndex < 0)
        {
            throw new RlpException("Negative firstBlockReceiptIndex is invalid");
        }

        V63.Messages.GetReceiptsMessage ethMessage = _innerSerializer.Deserialize(byteBuffer);
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
