// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class PooledTransactionsMessageSerializer : IMessageSerializer<PooledTransactionsMessage>
    {
        private readonly TransactionsMessageSerializer _txsMessageDeserializer = new();

        public int Serialize(Span<byte> destination, PooledTransactionsMessage message) => _txsMessageDeserializer.Serialize(destination, message);

        public PooledTransactionsMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            return new(TransactionsMessageSerializer.DeserializeTxs(ref ctx));
        }

        public int GetLength(PooledTransactionsMessage message) => _txsMessageDeserializer.GetLength(message);
    }
}
