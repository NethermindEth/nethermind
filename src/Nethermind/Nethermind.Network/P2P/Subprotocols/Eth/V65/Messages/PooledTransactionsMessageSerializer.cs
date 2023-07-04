// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class PooledTransactionsMessageSerializer : IZeroInnerMessageSerializer<PooledTransactionsMessage>
    {
        private readonly TransactionsMessageSerializer _txsMessageDeserializer = new();

        public void Serialize(IByteBuffer byteBuffer, PooledTransactionsMessage message)
        {
            _txsMessageDeserializer.Serialize(byteBuffer, message);
        }

        public PooledTransactionsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            Transaction[] txs = _txsMessageDeserializer.DeserializeTxs(rlpStream);
            return new PooledTransactionsMessage(txs);
        }

        public int GetLength(PooledTransactionsMessage message, out int contentLength)
        {
            return _txsMessageDeserializer.GetLength(message, out contentLength);
        }
    }
}
