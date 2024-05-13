// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class ReceiptsMessageInnerSerializer: V63.Messages.ReceiptsMessageSerializer, IZeroInnerMessageSerializer<ReceiptsInnerMessage>
{
    public ReceiptsMessageInnerSerializer(ISpecProvider specProvider): base(specProvider) { }

    protected override RlpBehaviors GetEncodingBehavior(TxReceipt txReceipt) =>
        base.GetEncodingBehavior(txReceipt) | RlpBehaviors.Eip7642Receipts;

    protected override RlpBehaviors GetDecodingBehavior() =>
        base.GetDecodingBehavior() | RlpBehaviors.Eip7642Receipts;

    int IZeroInnerMessageSerializer<ReceiptsInnerMessage>.GetLength(ReceiptsInnerMessage message, out int contentLength) =>
        GetLength(message, out contentLength);

    void IZeroMessageSerializer<ReceiptsInnerMessage>.Serialize(IByteBuffer byteBuffer, ReceiptsInnerMessage message) =>
        Serialize(byteBuffer, message);

    ReceiptsInnerMessage IZeroMessageSerializer<ReceiptsInnerMessage>.Deserialize(IByteBuffer byteBuffer)
    {
        V63.Messages.ReceiptsMessage? baseMessage = base.Deserialize(byteBuffer);
        return new ReceiptsInnerMessage(baseMessage.TxReceipts);
    }
}
