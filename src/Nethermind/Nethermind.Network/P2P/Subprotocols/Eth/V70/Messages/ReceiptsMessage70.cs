// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class ReceiptsMessage70 : V63.Messages.ReceiptsMessage, IEth66Message
{
    public long RequestId { get; set; } = MessageConstants.Random.NextLong();
    public bool LastBlockIncomplete { get; set; }

    public ReceiptsMessage70(IOwnedReadOnlyList<TxReceipt[]> txReceipts, bool lastBlockIncomplete)
        : this(MessageConstants.Random.NextLong(), txReceipts, lastBlockIncomplete)
    {
    }

    public ReceiptsMessage70(long requestId, IOwnedReadOnlyList<TxReceipt[]> txReceipts, bool lastBlockIncomplete)
        : base(txReceipts)
    {
        RequestId = requestId;
        LastBlockIncomplete = lastBlockIncomplete;
    }

    public ReceiptsMessage70(long requestId, V63.Messages.ReceiptsMessage ethMessage, bool lastBlockIncomplete)
        : this(requestId, ethMessage.TxReceipts, lastBlockIncomplete)
    {
    }

    public override string ToString() => $"Receipts70({RequestId}, incomplete={LastBlockIncomplete}, {base.ToString()})";
}
