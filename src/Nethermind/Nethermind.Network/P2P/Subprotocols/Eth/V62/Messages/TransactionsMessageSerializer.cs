// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
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
    public class TransactionsMessageSerializer : IZeroInnerMessageSerializer<TransactionsMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<TransactionsMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(TransactionsMessage.Transactions));
        private static readonly Nethermind.Serialization.Rlp.TxDecoder TxDecoder = Nethermind.Serialization.Rlp.TxDecoder.Instance;

        private readonly long _maxTxSize;

        /// <remarks>
        /// Approximates each blob's on-wire size via <c>GasPerBlob</c> (131,072 bytes) rather than the actual
        /// sidecar size (measured 131,176-131,184 bytes per blob, a ~626-byte shortfall at 6 blobs) - the same
        /// compensation <see cref="V68.Eth68ProtocolHandler"/> uses verbatim for eth/68 announcements.
        /// </remarks>
        private readonly long _maxBlobTxSize;

        // Cached once per instance: a lambda capturing `this` is not hoisted to a static delegate by the
        // compiler, so creating it inline on every Deserialize call would allocate a new delegate per message.
        private readonly DecodeRlpValue<TransactionsMessage> _deserializeTransactionsMessage;

        public TransactionsMessageSerializer(ITxPoolConfig? txPoolConfig = null, ISpecProvider? specProvider = null)
        {
            _maxTxSize = txPoolConfig?.MaxTxSize ?? long.MaxValue;
            _maxBlobTxSize = txPoolConfig?.MaxBlobTxSize is null || specProvider is null
                ? long.MaxValue
                : txPoolConfig.MaxBlobTxSize.Value + (long)specProvider.GetFinalMaxBlobGasPerBlock();
            _deserializeTransactionsMessage = (ref RlpReader ctx) => new TransactionsMessage(DeserializeTxsWithSizeGuard(ref ctx));
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

        /// <summary>Decodes the wire-form transaction list without a pre-decode size limit.</summary>
        public static IOwnedReadOnlyList<Transaction> DeserializeTxs(ref RlpReader ctx) =>
            DeserializeTxsCore(ref ctx, long.MaxValue, long.MaxValue);

        /// <summary>Decodes the wire-form transaction list, applying this serializer's configured pre-decode size caps.</summary>
        internal IOwnedReadOnlyList<Transaction> DeserializeTxsWithSizeGuard(ref RlpReader ctx) =>
            DeserializeTxsCore(ref ctx, _maxTxSize, _maxBlobTxSize);

        /// <remarks>
        /// An item whose pre-decode size exceeds <paramref name="maxTxSize"/> (or <paramref name="maxBlobTxSize"/>
        /// for a type-3 item) is skipped rather than decoded, avoiding the RLP-decode cost of an attacker-sized
        /// item - see <see cref="IsOverSizeLimit"/> for the measure.
        /// </remarks>
        private static IOwnedReadOnlyList<Transaction> DeserializeTxsCore(ref RlpReader ctx, long maxTxSize, long maxBlobTxSize)
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
                        Interlocked.Increment(ref Metrics.OversizedTransactionsSkipped);
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
        /// Mirrors <c>SizeTxFilter</c>'s post-decode measure (<c>tx.GetLength(shouldCountBlobs: false)</c>), so a
        /// transaction the pool would accept is never skipped here: a legacy item's comparable length includes
        /// its RLP prefix, but a typed item's mempool-form wrapper prefix is not part of that measure, so only
        /// its content length is compared.
        /// </remarks>
        private static bool IsOverSizeLimit(ref RlpReader ctx, long maxTxSize, long maxBlobTxSize)
        {
            bool isTyped = !ctx.IsSequenceNext();
            (int prefixLength, int contentLength) = ctx.PeekPrefixAndContentLength();
            long size = isTyped ? contentLength : prefixLength + (long)contentLength;

            if (size <= maxTxSize) return false;
            if (!isTyped) return true;

            // Safe only because MaxTxSize >= 55 (default 128 KiB): any item reaching this branch has content
            // length > MaxTxSize >= 55, which the RLP short-form's 55-byte cap cannot encode, so
            // PeekPrefixAndContentLength must have taken the bounds-checked long-form path. A smaller MaxTxSize
            // would let a truncated short-form item reach this peek unchecked.
            byte txType = ctx.Peek(prefixLength, 1)[0];
            return txType != (byte)TxType.Blob || size > maxBlobTxSize;
        }
    }
}
