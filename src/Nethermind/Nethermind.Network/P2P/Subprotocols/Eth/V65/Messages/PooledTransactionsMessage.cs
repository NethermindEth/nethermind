// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class PooledTransactionsMessage : TransactionsMessage
    {
        public override int PacketType { get; } = Eth65MessageCode.PooledTransactions;
        public override string Protocol { get; } = "eth";

        public PooledTransactionsMessage(IOwnedReadOnlyList<Transaction> transactions)
            : base(transactions)
        {
        }

        public override string ToString() => $"{nameof(PooledTransactionsMessage)}({Transactions?.Count})";
    }
}
