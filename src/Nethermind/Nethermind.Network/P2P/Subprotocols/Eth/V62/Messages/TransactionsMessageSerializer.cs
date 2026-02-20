// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class TransactionsMessageSerializer : IZeroInnerMessageSerializer<TransactionsMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<TransactionsMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(TransactionsMessage.Transactions));
        private readonly TxDecoder _decoder = TxDecoder.Instance;

        public void Serialize(IByteBuffer byteBuffer, TransactionsMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            NettyRlpStream nettyRlpStream = new(byteBuffer);

            nettyRlpStream.StartSequence(contentLength);
            foreach (Transaction tx in message.Transactions.AsSpan())
            {
                nettyRlpStream.Encode(tx, RlpBehaviors.InMempoolForm);
            }
        }

        public TransactionsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            IOwnedReadOnlyList<Transaction> txs = DeserializeTxs(rlpStream);
            return new TransactionsMessage(txs);
        }

        public int GetLength(TransactionsMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.Transactions.Count; i++)
            {
                contentLength += _decoder.GetLength(message.Transactions[i], RlpBehaviors.InMempoolForm);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        public static IOwnedReadOnlyList<Transaction> DeserializeTxs(RlpStream rlpStream)
        {
            return Rlp.DecodeArrayPool<Transaction>(rlpStream, RlpBehaviors.InMempoolForm, limit: RlpLimit);
        }
    }
}
