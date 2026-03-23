// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class ReceiptsMessageSerializer70(ISpecProvider specProvider)
    : Eth66MessageSerializer<ReceiptsMessage70>
{
    private readonly IZeroInnerMessageSerializer<ReceiptsInnerMessage69> _receiptsSerializer = new ReceiptsMessageInnerSerializer69(specProvider);

    protected override void SerializeInternal(IByteBuffer byteBuffer, ReceiptsMessage70 message)
    {
        ReceiptsInnerMessage69 inner = new(message.TxReceipts);

        NettyRlpStream stream = new(byteBuffer);
        stream.Encode(message.LastBlockIncomplete ? 1 : 0);
        _receiptsSerializer.Serialize(byteBuffer, inner);
    }

    protected override ReceiptsMessage70 DeserializeInternal(IByteBuffer byteBuffer, long requestId)
    {
        Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
        ulong lastBlockIncomplete = ctx.DecodeULong();

        if (lastBlockIncomplete is not 0 and not 1)
        {
            throw new RlpException($"{lastBlockIncomplete} is not correct value for {nameof(lastBlockIncomplete)}");
        }

        byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
        ReceiptsInnerMessage69 inner = _receiptsSerializer.Deserialize(byteBuffer);
        return new ReceiptsMessage70(requestId, inner, lastBlockIncomplete is 1);
    }

    protected override int GetLengthInternal(ReceiptsMessage70 message)
    {
        ReceiptsInnerMessage69 inner = new(message.TxReceipts);
        return Rlp.LengthOf(message.LastBlockIncomplete ? 1 : 0) +
            _receiptsSerializer.GetLength(inner, out _);
    }
}
