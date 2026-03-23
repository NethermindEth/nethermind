// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;
using GetReceiptsMessage63 = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.GetReceiptsMessage;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class GetReceiptsMessageSerializer70(IZeroInnerMessageSerializer<GetReceiptsMessage63> innerSerializer)
    : Eth66MessageSerializer<GetReceiptsMessage70>
{
    private readonly IZeroInnerMessageSerializer<GetReceiptsMessage63> _innerSerializer = innerSerializer;

    protected override void SerializeInternal(IByteBuffer byteBuffer, GetReceiptsMessage70 message)
    {
        NettyRlpStream stream = new(byteBuffer);
        stream.Encode(message.FirstBlockReceiptIndex);
        _innerSerializer.Serialize(byteBuffer, message);
    }

    protected override GetReceiptsMessage70 DeserializeInternal(IByteBuffer byteBuffer, long requestId)
    {
        Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
        long firstIndex = ctx.DecodeLong();

        if (firstIndex < 0)
        {
            throw new RlpException("Negative firstBlockReceiptIndex is invalid");
        }

        byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
        GetReceiptsMessage63 innerMessage = _innerSerializer.Deserialize(byteBuffer);
        return new GetReceiptsMessage70(requestId, firstIndex, innerMessage);
    }

    protected override int GetLengthInternal(GetReceiptsMessage70 message) =>
        Rlp.LengthOf(message.FirstBlockReceiptIndex) +
        _innerSerializer.GetLength(message, out _);
}
