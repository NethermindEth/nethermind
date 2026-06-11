// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class ReceiptsMessage70(
    IOwnedReadOnlyList<TxReceipt[]?>? txReceipts,
    bool lastBlockIncomplete,
    bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    public IOwnedReadOnlyList<TxReceipt[]?> TxReceipts { get; } = txReceipts ?? ArrayPoolList<TxReceipt[]?>.Empty();
    public bool LastBlockIncomplete { get; set; } = lastBlockIncomplete;

    public override int PacketType => Eth70MessageCode.Receipts;
    public override string Protocol => "eth";

    public ReceiptsMessage70(long requestId, IOwnedReadOnlyList<TxReceipt[]?>? txReceipts, bool lastBlockIncomplete)
        : this(txReceipts, lastBlockIncomplete, false) => RequestId = requestId;

    public override string ToString() => $"Receipts70({RequestId}, incomplete={LastBlockIncomplete}, {TxReceipts.Count})";

    public override void Dispose()
    {
        base.Dispose();
        TxReceipts.Dispose();
    }
}
