// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class TransactionsMessage : P2PMessage
    {
        // This is the target size for the packs of transactions. A pack can get larger than this if a single
        // transaction exceeds this size. This solution is similar to Geth one.
        public const int MaxPacketSize = 102400;

        public override int PacketType { get; } = Eth62MessageCode.Transactions;
        public override string Protocol { get; } = "eth";
        public IOwnedReadOnlyList<Transaction> Transactions { get; }

        public TransactionsMessage(IOwnedReadOnlyList<Transaction> transactions)
        {
            Transactions = transactions;
        }

        public override string ToString() => $"{nameof(TransactionsMessage)}({Transactions?.Count})";

        public override void Dispose()
        {
            base.Dispose();
            Transactions?.Dispose();
        }
    }
}
