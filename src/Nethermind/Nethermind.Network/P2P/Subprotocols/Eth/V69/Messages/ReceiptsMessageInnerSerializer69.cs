// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class ReceiptsMessageInnerSerializer69 : V63.Messages.ReceiptsMessageSerializer, IZeroInnerMessageSerializer<ReceiptsInnerMessage69>
{
    public ReceiptsMessageInnerSerializer69(ISpecProvider specProvider) : base(specProvider) { }

    protected override RlpBehaviors GetEncodingBehavior(TxReceipt txReceipt) =>
        base.GetEncodingBehavior(txReceipt) | RlpBehaviors.Eip7642Messages;

    protected override RlpBehaviors GetDecodingBehavior() =>
        base.GetDecodingBehavior() | RlpBehaviors.Eip7642Messages;

    int IZeroInnerMessageSerializer<ReceiptsInnerMessage69>.GetLength(ReceiptsInnerMessage69 message, out int contentLength) =>
        GetLength(message, out contentLength);

    void IZeroMessageSerializer<ReceiptsInnerMessage69>.Serialize(IByteBuffer byteBuffer, ReceiptsInnerMessage69 message) =>
        Serialize(byteBuffer, message);

    ReceiptsInnerMessage69 IZeroMessageSerializer<ReceiptsInnerMessage69>.Deserialize(IByteBuffer byteBuffer)
    {
        V63.Messages.ReceiptsMessage? baseMessage = base.Deserialize(byteBuffer);
        return new ReceiptsInnerMessage69(baseMessage.TxReceipts);
    }
}
