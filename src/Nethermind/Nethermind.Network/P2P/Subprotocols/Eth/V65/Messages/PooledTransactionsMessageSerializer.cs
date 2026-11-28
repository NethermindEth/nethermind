// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using EncodedTransaction = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage.EncodedTransaction;
using TransactionDecoder = Nethermind.Serialization.Rlp.TxDecoder;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class PooledTransactionsMessageSerializer : IMessageSerializer<PooledTransactionsMessage>
    {
        private readonly TransactionsMessageSerializer _txsMessageDeserializer = new();

        public int Serialize(Span<byte> destination, PooledTransactionsMessage message)
        {
            if (message.EncodedTransactions is not { } encodedTransactions)
            {
                return _txsMessageDeserializer.Serialize(destination, message);
            }

            RlpWriter writer = RlpWriter.ContentWriter(destination);
            RlpSequence sequence = writer.StartSequence(GetContentLength(message, encodedTransactions));
            ReadOnlySpan<MempoolTransaction> transactions = message.Transactions.AsSpan();
            int written = 0;
            foreach (EncodedTransaction encodedTransaction in encodedTransactions.AsSpan())
            {
                for (; written < encodedTransaction.TransactionIndex; written++)
                {
                    MempoolTxDecoder.Instance.Encode(ref writer, transactions[written]);
                }

                ReadOnlySpan<byte> encoding = encodedTransaction.Encoding.AsSpan();
                writer.StartByteArray(encoding.Length, false);
                writer.WriteEncoded(encoding);
            }

            for (; written < transactions.Length; written++)
            {
                MempoolTxDecoder.Instance.Encode(ref writer, transactions[written]);
            }

            writer.EndSequence(sequence);
            return writer.Length;
        }

        public PooledTransactionsMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            return new(TransactionsMessageSerializer.DeserializeTxs(ref ctx));
        }

        public int GetLength(PooledTransactionsMessage message) => message.EncodedTransactions is { } encodedTransactions
            ? Rlp.LengthOfSequence(GetContentLength(message, encodedTransactions))
            : _txsMessageDeserializer.GetLength(message);

        private static int GetContentLength(PooledTransactionsMessage message, IOwnedReadOnlyList<EncodedTransaction> encodedTransactions)
        {
            int contentLength = TransactionsMessageSerializer.GetContentLength(message);
            foreach (EncodedTransaction encodedTransaction in encodedTransactions.AsSpan())
            {
                contentLength += TransactionDecoder.GetWrappedTxLength(TxType.Blob, encodedTransaction.Encoding.Count);
            }

            return contentLength;
        }
    }
}
