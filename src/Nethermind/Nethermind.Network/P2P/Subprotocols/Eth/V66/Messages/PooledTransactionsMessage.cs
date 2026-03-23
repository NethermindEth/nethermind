// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class PooledTransactionsMessage : V65.Messages.PooledTransactionsMessage, IEth66Message
    {
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();

        public PooledTransactionsMessage(long requestId, IOwnedReadOnlyList<Transaction> transactions)
            : base(transactions)
        {
            RequestId = requestId;
        }

        public PooledTransactionsMessage(long requestId, V65.Messages.PooledTransactionsMessage message)
            : this(requestId, message.Transactions)
        {
        }
    }
}
