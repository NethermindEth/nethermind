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
        /// <summary>
        /// Maximum total RLP elements allowed in a transactions message.
        /// Prevents nested amplification DOS (e.g., transactions × access list entries × storage keys).
        /// Set to 2M to allow legitimate large tx batches (each tx has ~10 RLP fields) while preventing DOS.
        /// </summary>
        private const int MaxTotalElements = 2_000_000;

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
            IOwnedReadOnlyList<Transaction> txs = DeserializeTxs(rlpStream, MaxTotalElements);
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

        public static IOwnedReadOnlyList<Transaction> DeserializeTxs(RlpStream rlpStream, int maxTotalElements = MaxTotalElements)
        {
            // Pass 1: Validate nested structure to prevent memory DOS
            RlpElementCounter.CountElementsInSequence(rlpStream, maxTotalElements);

            // Pass 2: Actual decode (limits validated, safe to allocate)
            rlpStream.Position = 0;
            return Rlp.DecodeArrayPool<Transaction>(rlpStream, RlpBehaviors.InMempoolForm, limit: RlpLimit);
        }
    }
}
