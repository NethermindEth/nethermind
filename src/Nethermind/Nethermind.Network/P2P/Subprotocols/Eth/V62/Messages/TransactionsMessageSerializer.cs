// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class TransactionsMessageSerializer(ITxPoolConfig? txPoolConfig = null, ISpecProvider? specProvider = null) : IZeroInnerMessageSerializer<TransactionsMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<TransactionsMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(TransactionsMessage.Transactions));
        private static readonly Nethermind.Serialization.Rlp.TxDecoder TxDecoder = Nethermind.Serialization.Rlp.TxDecoder.Instance;

        private readonly long _maxTxSize = txPoolConfig?.MaxTxSize ?? long.MaxValue;
        private readonly long _maxBlobTxSize = txPoolConfig?.MaxBlobTxSize is null || specProvider is null
            ? long.MaxValue
            : txPoolConfig.MaxBlobTxSize.Value + (long)specProvider.GetFinalMaxBlobGasPerBlock();

        /// <summary>The configured pre-decode size cap for a non-blob transaction, shared with <see cref="V65.Messages.PooledTransactionsMessageSerializer"/>.</summary>
        internal long MaxTxSize => _maxTxSize;

        /// <summary>The configured pre-decode size cap for a blob transaction, shared with <see cref="V65.Messages.PooledTransactionsMessageSerializer"/>.</summary>
        internal long MaxBlobTxSize => _maxBlobTxSize;

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
            byteBuffer.DeserializeRlp((ref RlpReader ctx) => new TransactionsMessage(DeserializeTxs(ref ctx, _maxTxSize, _maxBlobTxSize)));

        public int GetLength(TransactionsMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.Transactions.Count; i++)
            {
                contentLength += TxDecoder.GetLength(message.Transactions[i], RlpBehaviors.InMempoolForm);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        /// <summary>Decodes the wire-form transaction list.</summary>
        /// <remarks>
        /// An item whose pre-decode size exceeds <paramref name="maxTxSize"/> (or <paramref name="maxBlobTxSize"/>
        /// for a type-3 item) is skipped rather than decoded, so a peer that sends an over-limit transaction is
        /// not disconnected by the resulting decode failure - see <see cref="IsOverSizeLimit"/> for the measure.
        /// </remarks>
        public static IOwnedReadOnlyList<Transaction> DeserializeTxs(ref RlpReader ctx, long maxTxSize = long.MaxValue, long maxBlobTxSize = long.MaxValue)
        {
            int checkPosition = ctx.ReadSequenceLength() + ctx.Position;
            int length = ctx.PeekNumberOfItemsRemaining(checkPosition);
            ctx.GuardLimit(length, RlpLimit);

            ArrayPoolList<Transaction> result = new(length);
            try
            {
                for (int i = 0; i < length; i++)
                {
                    if (IsOverSizeLimit(ref ctx, maxTxSize, maxBlobTxSize))
                    {
                        ctx.SkipItem();
                        continue;
                    }

                    result.Add(TxDecoder.DecodeGuardNotNull(ref ctx, RlpBehaviors.InMempoolForm));
                }
                ctx.Check(checkPosition);
                return result;
            }
            catch
            {
                foreach (Transaction tx in result)
                    tx.ClearPreHash();
                result.Dispose();
                throw;
            }
        }

        /// <summary>Whether the item at the reader's current position exceeds the configured size cap.</summary>
        /// <remarks>
        /// Mirrors <see cref="Nethermind.TxPool.Filters.SizeTxFilter"/>'s post-decode measure
        /// (<c>tx.GetLength(shouldCountBlobs: false)</c>), so a transaction the pool would accept is never
        /// skipped here: a legacy item's comparable length includes its RLP prefix, but a typed item's
        /// mempool-form wrapper prefix is not part of that measure, so only its content length is compared.
        /// </remarks>
        private static bool IsOverSizeLimit(ref RlpReader ctx, long maxTxSize, long maxBlobTxSize)
        {
            bool isTyped = !ctx.IsSequenceNext();
            (int prefixLength, int contentLength) = ctx.PeekPrefixAndContentLength();
            long size = isTyped ? contentLength : prefixLength + (long)contentLength;

            if (size <= maxTxSize) return false;
            if (!isTyped) return true;

            // Items over MaxTxSize (128 KiB by default) are necessarily long-form, well above the 55-byte
            // short-form cutoff, so PeekPrefixAndContentLength has already taken the long-form path and
            // rejected a lying length header - the type byte right after it needs no bounds check of its own.
            byte txType = ctx.Peek(prefixLength, 1)[0];
            return txType != (byte)TxType.Blob || size > maxBlobTxSize;
        }
    }
}
