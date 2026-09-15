// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class PooledTransactionsMessageSerializer : IZeroInnerMessageSerializer<PooledTransactionsMessage>
    {
        private readonly TransactionsMessageSerializer _txsMessageDeserializer;

        // Cached once per instance - see the identical note on TransactionsMessageSerializer's own field.
        private readonly DecodeRlpValue<PooledTransactionsMessage> _deserializePooledTransactionsMessage;

        public PooledTransactionsMessageSerializer(ITxPoolConfig? txPoolConfig = null, ISpecProvider? specProvider = null)
        {
            _txsMessageDeserializer = new(txPoolConfig, specProvider);
            _deserializePooledTransactionsMessage = (ref RlpReader ctx) => new PooledTransactionsMessage(_txsMessageDeserializer.DeserializeTxsWithSizeGuard(ref ctx));
        }

        public void Serialize(IByteBuffer byteBuffer, PooledTransactionsMessage message) => _txsMessageDeserializer.Serialize(byteBuffer, message);

        public PooledTransactionsMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(_deserializePooledTransactionsMessage);

        public int GetLength(PooledTransactionsMessage message, out int contentLength) => _txsMessageDeserializer.GetLength(message, out contentLength);
    }
}
