// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
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

        [ThreadStatic]
        private static IKnownTransactionFilter? t_knownTransactionFilter;

        public TransactionsMessageSerializer(ITxPoolConfig? txPoolConfig = null)
        {
            _maxTxSize = Math.Max(txPoolConfig?.MaxTxSize ?? long.MaxValue, ShortFormMaxItemMeasure);
            _deserializeTransactionsMessage = (ref RlpReader ctx) =>
            {
                IOwnedReadOnlyList<Transaction> transactions = DeserializeTxs(ref ctx, _maxTxSize, t_knownTransactionFilter, out int skippedCount);
                return new TransactionsMessage(transactions) { SkippedCount = skippedCount };
            };
        }

        /// <summary>
        /// Lets <paramref name="filter"/> settle already-known transactions by hash, so they are skipped undecoded,
        /// for the <see cref="TransactionsMessage"/> deserialized on this thread until the scope is disposed.
        /// </summary>
        /// <remarks>
        /// A thread-local scope rather than a parameter because the message is deserialized through
        /// <see cref="IMessageSerializationService"/>, which carries no per-call state. Only the legacy, access-list,
        /// EIP-1559 and set-code types are checked: their hash is the Keccak of the wire item, unlike a blob
        /// transaction's network form.
        /// </remarks>
        internal static KnownTransactionScope SkipKnown(IKnownTransactionFilter? filter)
        {
            t_knownTransactionFilter = filter;
            return default;
        }

        internal readonly ref struct KnownTransactionScope
        {
            public void Dispose() => t_knownTransactionFilter = null;
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
            DeserializeTxs(ref ctx, long.MaxValue, knownFilter: null, out _);

        internal static int ReadTransactionCount(ref RlpReader ctx, out int checkPosition)
        {
            checkPosition = ctx.ReadSequenceLength() + ctx.Position;
            int count = ctx.PeekNumberOfItemsRemaining(checkPosition, RlpLimit.Limit + 1);
            ctx.GuardLimit(count, RlpLimit);
            return count;
        }

        /// <summary>Decodes the wire-form transaction list, skipping any item whose pre-decode size exceeds <paramref name="maxTxSize"/>.</summary>
        /// <remarks>Avoids the RLP-decode cost of an attacker-sized item - see <see cref="IsOverSizeLimit"/> for the measure.</remarks>
        /// <param name="maxTxSize">The size cap, or <see cref="long.MaxValue"/> to decode every item without measuring it.</param>
        /// <param name="knownFilter">Settles already-known items, which are then skipped; <see langword="null"/> decodes every item.</param>
        /// <param name="skippedCount">The number of items skipped for exceeding the cap.</param>
        private static IOwnedReadOnlyList<Transaction> DeserializeTxs(ref RlpReader ctx, long maxTxSize, IKnownTransactionFilter? knownFilter, out int skippedCount)
        {
            int length = ReadTransactionCount(ref ctx, out int checkPosition);

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

                    if (knownFilter is not null && TryHashWireItem(ref ctx, out ValueHash256 hash, out TxType type, out int size))
                    {
                        if (knownFilter.TrySettleKnown(in hash, type, size))
                        {
                            ctx.SkipItem();
                            continue;
                        }

                        Transaction tx = TxDecoder.DecodeGuardNotNull(ref ctx, RlpBehaviors.InMempoolForm | RlpBehaviors.PoolBlobBuffers | RlpBehaviors.ExcludeHashes);
                        tx.Hash = new Hash256(in hash);
                        result.Add(tx);
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

        /// <summary>Hashes the transaction at the reader's current position without decoding it, for the types whose hash is the Keccak of the wire item.</summary>
        /// <param name="size">The length of the hashed bytes, which is the transaction's network encoding size.</param>
        private static bool TryHashWireItem(ref RlpReader ctx, out ValueHash256 hash, out TxType type, out int size)
        {
            if (ctx.IsSequenceNext())
            {
                ReadOnlySpan<byte> item = ctx.PeekNextItem();
                hash = ValueKeccak.Compute(item);
                type = TxType.Legacy;
                size = item.Length;
                return true;
            }

            (int prefixLength, int contentLength) = ctx.PeekPrefixAndContentLength();
            if (contentLength > 0 && ctx.Peek(prefixLength, 1)[0] is (byte)TxType.AccessList or (byte)TxType.EIP1559 or (byte)TxType.SetCode)
            {
                ReadOnlySpan<byte> content = ctx.Peek(prefixLength, contentLength);
                hash = ValueKeccak.Compute(content);
                type = (TxType)content[0];
                size = contentLength;
                return true;
            }

            hash = default;
            type = default;
            size = 0;
            return false;
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
