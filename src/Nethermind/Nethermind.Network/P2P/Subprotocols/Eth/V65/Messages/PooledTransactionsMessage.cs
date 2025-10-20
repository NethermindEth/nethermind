// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class PooledTransactionsMessage(IOwnedReadOnlyList<Transaction> transactions)
        : TransactionsMessage(transactions)
    {
        public override int PacketType => Eth65MessageCode.PooledTransactions;
        public override string Protocol => "eth";

        public override string ToString() => $"{nameof(PooledTransactionsMessage)}({Transactions?.Count})";
    }
}
