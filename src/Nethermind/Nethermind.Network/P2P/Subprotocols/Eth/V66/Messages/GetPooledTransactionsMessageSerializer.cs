// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetPooledTransactionsMessageSerializer : Eth66MessageSerializer<GetPooledTransactionsMessage, V65.Messages.GetPooledTransactionsMessage>
    {
        public GetPooledTransactionsMessageSerializer() : base(new V65.Messages.GetPooledTransactionsMessageSerializer())
        {
        }
    }
}
