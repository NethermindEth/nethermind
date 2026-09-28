// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;

namespace Nethermind.Xdc.P2P.Messages;

/// <summary>
/// <see cref="PooledTransactionsMessage"/> on the code XDC relocated it to.
/// </summary>
/// <inheritdoc cref="XdcNewPooledTransactionHashesMessage" path="/remarks"/>
public class XdcPooledTransactionsMessage(IOwnedReadOnlyList<Transaction> transactions) : PooledTransactionsMessage(transactions)
{
    public override int PacketType => XdcMessageCode.PooledTransactions;

    public override string ToString() => $"{nameof(XdcPooledTransactionsMessage)}({Transactions?.Count})";
}
