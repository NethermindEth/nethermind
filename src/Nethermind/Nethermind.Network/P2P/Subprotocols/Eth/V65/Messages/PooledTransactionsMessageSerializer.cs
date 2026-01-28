// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class PooledTransactionsMessageSerializer : IZeroInnerMessageSerializer<PooledTransactionsMessage>
    {
        /// <summary>
        /// Maximum total RLP elements allowed in a pooled transactions message.
        /// Same as TransactionsMessageSerializer limit.
        /// </summary>
        private const int MaxTotalElements = 2_000_000;

        private readonly TransactionsMessageSerializer _txsMessageDeserializer = new();

        public void Serialize(IByteBuffer byteBuffer, PooledTransactionsMessage message)
        {
            _txsMessageDeserializer.Serialize(byteBuffer, message);
        }

        public PooledTransactionsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            // DeserializeTxs includes element count validation
            IOwnedReadOnlyList<Transaction> txs = TransactionsMessageSerializer.DeserializeTxs(rlpStream, MaxTotalElements);
            return new PooledTransactionsMessage(txs);
        }

        public int GetLength(PooledTransactionsMessage message, out int contentLength)
        {
            return _txsMessageDeserializer.GetLength(message, out contentLength);
        }
    }
}
