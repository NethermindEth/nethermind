// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class ReceiptsMessageSerializer70(ISpecProvider specProvider)
    : Eth66SerializerBase<ReceiptsMessage70>
{
    private readonly ReceiptsMessageInnerSerializer69 _receiptsSerializer = new(specProvider);

    protected override void SerializeInternal(IByteBuffer byteBuffer, ReceiptsMessage70 message, ref LengthStack lengths)
    {
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.Encode(message.LastBlockIncomplete ? 1 : 0);
        _receiptsSerializer.Serialize(byteBuffer, new ReceiptsInnerMessage69(message.TxReceipts), ref lengths);
    }

    protected override ReceiptsMessage70 DeserializeInternal(ref RlpReader ctx, long requestId)
    {
        ulong lastBlockIncomplete = ctx.DecodeULong();

        if (lastBlockIncomplete is not 0 and not 1)
        {
            throw new RlpException($"{lastBlockIncomplete} is not correct value for {nameof(lastBlockIncomplete)}");
        }

        V63.Messages.ReceiptsMessage inner = _receiptsSerializer.Deserialize(ref ctx);
        return new ReceiptsMessage70(requestId, inner.TxReceipts, lastBlockIncomplete is 1);
    }

    protected override void PopulateLengthInternal(ReceiptsMessage70 message, ref LengthStack lengths)
    {
        _receiptsSerializer.PopulateLength(new ReceiptsInnerMessage69(message.TxReceipts), ref lengths);
        lengths.Push(Rlp.LengthOf(message.LastBlockIncomplete ? 1 : 0) + lengths.Pop());
    }
}
