// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetPooledTransactionsMessage : Eth66Message<V65.Messages.GetPooledTransactionsMessage>
    {
        public GetPooledTransactionsMessage()
        {
        }

        public GetPooledTransactionsMessage(long requestId, V65.Messages.GetPooledTransactionsMessage ethMessage) : base(requestId, ethMessage)
        {
        }
    }
}
