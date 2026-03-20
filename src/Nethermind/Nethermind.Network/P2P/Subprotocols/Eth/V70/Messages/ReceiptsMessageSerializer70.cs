// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class ReceiptsMessageSerializer70(ISpecProvider specProvider)
    : Eth66SerializerBase<ReceiptsMessage70>
{
    private readonly ReceiptsMessageInnerSerializer69 _receiptsSerializer = new(specProvider);

    protected override void SerializeInternal(IByteBuffer byteBuffer, ReceiptsMessage70 message)
    {
        NettyRlpStream stream = new(byteBuffer);
        stream.Encode(message.LastBlockIncomplete ? 1 : 0);
        ReceiptsInnerMessage69 inner = new(message.TxReceipts);
        _receiptsSerializer.Serialize(byteBuffer, inner);
    }

    protected override ReceiptsMessage70 DeserializeInternal(ref Rlp.ValueDecoderContext ctx, long requestId)
    {
        ulong lastBlockIncomplete = ctx.DecodeULong();

        if (lastBlockIncomplete is not 0 and not 1)
        {
            throw new RlpException($"{lastBlockIncomplete} is not correct value for {nameof(lastBlockIncomplete)}");
        }

        V63.Messages.ReceiptsMessage inner = _receiptsSerializer.Deserialize(ref ctx);
        return new ReceiptsMessage70(requestId, inner.TxReceipts, lastBlockIncomplete is 1);
    }

    protected override int GetLengthInternal(ReceiptsMessage70 message)
    {
        ReceiptsInnerMessage69 inner = new(message.TxReceipts);
        int receiptsLength = _receiptsSerializer.GetLength(inner, out _);

        return Rlp.LengthOf(message.LastBlockIncomplete ? 1 : 0) + receiptsLength;
    }
}
