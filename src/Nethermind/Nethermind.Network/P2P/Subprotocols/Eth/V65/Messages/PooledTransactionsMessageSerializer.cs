// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class PooledTransactionsMessageSerializer : IZeroMessageSerializer<PooledTransactionsMessage>
    {
        private readonly TransactionsMessageSerializer _txsMessageDeserializer = new();

        public void Serialize(Span<byte> buffer, PooledTransactionsMessage message) => _txsMessageDeserializer.Serialize(buffer, message);

        public PooledTransactionsMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            PooledTransactionsMessage msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }

        private static PooledTransactionsMessage Deserialize(ref RlpReader ctx) =>
            new(TransactionsMessageSerializer.DeserializeTxs(ref ctx));

        public int GetLength(PooledTransactionsMessage message, out int contentLength) => _txsMessageDeserializer.GetLength(message, out contentLength);
    }
}
