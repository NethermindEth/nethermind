// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class ReceiptsMessage70 : Eth66MessageBase
{
    public IOwnedReadOnlyList<TxReceipt[]?> TxReceipts { get; }
    public bool LastBlockIncomplete { get; set; }

    public override int PacketType => Eth70MessageCode.Receipts;
    public override string Protocol => "eth";

    public ReceiptsMessage70(IOwnedReadOnlyList<TxReceipt[]> txReceipts, bool lastBlockIncomplete, bool generateRandomRequestId = true)
        : base(generateRandomRequestId)
    {
        TxReceipts = txReceipts ?? ArrayPoolList<TxReceipt[]>.Empty();
        LastBlockIncomplete = lastBlockIncomplete;
    }

    public ReceiptsMessage70(long requestId, IOwnedReadOnlyList<TxReceipt[]> txReceipts, bool lastBlockIncomplete)
        : this(txReceipts, lastBlockIncomplete, false)
    {
        RequestId = requestId;
    }

    public override string ToString() => $"Receipts70({RequestId}, incomplete={LastBlockIncomplete}, {TxReceipts.Count})";

    public override void Dispose()
    {
        base.Dispose();
        TxReceipts.Dispose();
    }
}
