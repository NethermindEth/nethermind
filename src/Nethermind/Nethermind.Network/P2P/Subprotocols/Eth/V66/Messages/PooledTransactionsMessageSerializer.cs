// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class PooledTransactionsMessageSerializer : Eth66MessageSerializer<PooledTransactionsMessage, V65.Messages.PooledTransactionsMessage>
    {
        public PooledTransactionsMessageSerializer() : base(new V65.Messages.PooledTransactionsMessageSerializer())
        {
        }
    }
}
