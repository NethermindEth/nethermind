// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class PooledTransactionsMessageSerializer(ITxPoolConfig? txPoolConfig = null, ISpecProvider? specProvider = null) : IZeroInnerMessageSerializer<PooledTransactionsMessage>
    {
        private readonly TransactionsMessageSerializer _txsMessageDeserializer = new(txPoolConfig, specProvider);

        public void Serialize(IByteBuffer byteBuffer, PooledTransactionsMessage message) => _txsMessageDeserializer.Serialize(byteBuffer, message);

        public PooledTransactionsMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp((ref RlpReader ctx) =>
                new PooledTransactionsMessage(TransactionsMessageSerializer.DeserializeTxs(ref ctx, _txsMessageDeserializer.MaxTxSize, _txsMessageDeserializer.MaxBlobTxSize)));

        public int GetLength(PooledTransactionsMessage message, out int contentLength) => _txsMessageDeserializer.GetLength(message, out contentLength);
    }
}
