// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;
using Nethermind.TxPool;
using TransactionDecoder = Nethermind.Serialization.Rlp.TxDecoder;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class TransactionsMessageSerializer : IZeroInnerMessageSerializer<TransactionsMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<TransactionsMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(TransactionsMessage.Transactions));
        private static readonly TransactionDecoder TxDecoder = TransactionDecoder.Instance;

        /// <summary>The largest measure <see cref="IsOverSizeLimit"/> can read off an RLP short-form item.</summary>
        /// <remarks>
        /// The configured cap is clamped to this floor so a small limit still can't push the type-byte peek or
        /// the unchecked <c>SkipItem</c> past a bounds-checked long-form read (Yellow Paper, Appendix B: 55
        /// content bytes).
        /// </remarks>
        private const long ShortFormMaxItemMeasure = 56;

        private readonly long _maxTxSize;

        // Cached once per instance to avoid allocating a new delegate on every Deserialize call.
        private readonly DecodeRlpValue<TransactionsMessage> _deserializeTransactionsMessage;

        public TransactionsMessageSerializer(ITxPoolConfig? txPoolConfig = null)
        {
            _maxTxSize = Math.Max(txPoolConfig?.MaxTxSize ?? long.MaxValue, ShortFormMaxItemMeasure);
            _deserializeTransactionsMessage = (ref RlpReader ctx) =>
            {
                IOwnedReadOnlyList<Transaction> transactions = DeserializeTxs(ref ctx, _maxTxSize, out int skippedCount);
                return new TransactionsMessage(transactions) { SkippedCount = skippedCount };
            };
        }

        public void Serialize(IByteBuffer byteBuffer, TransactionsMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            ByteBufferRlpWriter writer = new(byteBuffer);

            writer.StartSequence(contentLength);
            foreach (Transaction tx in message.Transactions.AsSpan())
            {
                TxDecoder.Encode(ref writer, tx, RlpBehaviors.InMempoolForm);
            }
        }

        public TransactionsMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(_deserializeTransactionsMessage);

        public int GetLength(TransactionsMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.Transactions.Count; i++)
            {
                contentLength += TxDecoder.GetLength(message.Transactions[i], RlpBehaviors.InMempoolForm);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        public static IOwnedReadOnlyList<Transaction> DeserializeTxs(ref RlpReader ctx) =>
            DeserializeTxs(ref ctx, long.MaxValue, out _);

        /// <summary>Decodes the wire-form transaction list, skipping any item whose pre-decode size exceeds <paramref name="maxTxSize"/>.</summary>
        /// <remarks>Avoids the RLP-decode cost of an attacker-sized item - see <see cref="IsOverSizeLimit"/> for the measure.</remarks>
        /// <param name="maxTxSize">The size cap, or <see cref="long.MaxValue"/> to decode every item without measuring it.</param>
        /// <param name="skippedCount">The number of items skipped for exceeding the cap.</param>
        private static IOwnedReadOnlyList<Transaction> DeserializeTxs(ref RlpReader ctx, long maxTxSize, out int skippedCount)
        {
            int checkPosition = ctx.ReadSequenceLength() + ctx.Position;
            int length = ctx.PeekNumberOfItemsRemaining(checkPosition);
            ctx.GuardLimit(length, RlpLimit);

            bool isSizeGuarded = maxTxSize != long.MaxValue;
            int skipped = 0;
            ArrayPoolList<Transaction> result = new(length);
            try
            {
                for (int i = 0; i < length; i++)
                {
                    if (isSizeGuarded && IsOverSizeLimit(ref ctx, maxTxSize))
                    {
                        ctx.SkipItem();
                        skipped++;
                        Interlocked.Increment(ref Metrics.OversizedTransactionsSkipped);
                        continue;
                    }

                    result.Add(TxDecoder.DecodeGuardNotNull(ref ctx, RlpBehaviors.InMempoolForm | RlpBehaviors.PoolBlobBuffers));
                }
                ctx.Check(checkPosition);
                skippedCount = skipped;
                return result;
            }
            catch
            {
                foreach (Transaction tx in result)
                {
                    tx.ClearPreHash();
                    TransactionDecoder.TxObjectPool.Return(tx);
                }
                result.Dispose();
                throw;
            }
        }

        /// <summary>Whether the item at the reader's current position exceeds the configured size cap.</summary>
        /// <remarks>
        /// The type-byte peek only runs once size has already exceeded <see cref="ShortFormMaxItemMeasure"/>, so
        /// it always follows a bounds-checked long-form length read that guarantees the byte is present.
        /// </remarks>
        private static bool IsOverSizeLimit(ref RlpReader ctx, long maxTxSize)
        {
            bool isTyped = !ctx.IsSequenceNext();
            (int prefixLength, int contentLength) = ctx.PeekPrefixAndContentLength();
            long size = isTyped ? contentLength : prefixLength + (long)contentLength;

            if (size <= maxTxSize) return false;
            if (!isTyped) return true;

            return ctx.Peek(prefixLength, 1)[0] != (byte)TxType.Blob;
        }
    }
}
