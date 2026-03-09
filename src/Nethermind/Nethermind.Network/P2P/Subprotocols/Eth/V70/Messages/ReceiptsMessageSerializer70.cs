// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class ReceiptsMessageSerializer70(ISpecProvider specProvider)
    : IZeroInnerMessageSerializer<ReceiptsMessage70>
{
    private readonly ReceiptsMessageInnerSerializer69 _receiptsSerializer = new(specProvider);

    public void Serialize(IByteBuffer byteBuffer, ReceiptsMessage70 message)
    {
        ReceiptsInnerMessage69 inner = new(message.EthMessage.TxReceipts);

        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);

        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        stream.Encode(message.RequestId);
        stream.Encode(message.LastBlockIncomplete ? 1 : 0);
        _receiptsSerializer.Serialize(byteBuffer, inner);
    }

    public ReceiptsMessage70 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private ReceiptsMessage70 Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        ctx.ReadSequenceLength();

        long requestId = ctx.DecodeLong();
        ulong lastBlockIncomplete = ctx.DecodeULong();

        if (lastBlockIncomplete is not 0 and not 1)
        {
            throw new RlpException($"{lastBlockIncomplete} is not correct value for {nameof(lastBlockIncomplete)}");
        }

        ReceiptsMessage inner = _receiptsSerializer.Deserialize(ref ctx);
        return new ReceiptsMessage70(requestId, inner, lastBlockIncomplete is 1);
    }

    public int GetLength(ReceiptsMessage70 message, out int contentLength)
    {
        ReceiptsInnerMessage69 inner = new(message.EthMessage.TxReceipts);
        int receiptsLength = _receiptsSerializer.GetLength(inner, out _);

        contentLength =
            Rlp.LengthOf(message.RequestId) +
            Rlp.LengthOf(message.LastBlockIncomplete ? 1 : 0) +
            receiptsLength;

        return Rlp.LengthOfSequence(contentLength);
    }
}
