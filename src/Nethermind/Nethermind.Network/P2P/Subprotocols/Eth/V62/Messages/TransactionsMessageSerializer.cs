// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        /// <summary>The largest content length an RLP short form can encode (Yellow Paper, Appendix B).</summary>
        /// <remarks>
        /// <c>PeekPrefixAndContentLength</c> bounds-checks only the long forms, so the bytes an item declares
        /// past its prefix are safe to read only once its declared length rules the short forms out. Both caps
        /// below are clamped to this length, which keeps every item reaching the type-byte peek in
        /// <see cref="IsOverSizeLimit"/> in the bounds-checked long form however small a limit is configured.
        /// </remarks>
        private const long ShortFormMaxContentLength = 55;

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
            _maxTxSize = Math.Max(txPoolConfig?.MaxTxSize ?? long.MaxValue, ShortFormMaxContentLength);
            _maxBlobTxSize = txPoolConfig?.MaxBlobTxSize is null || specProvider is null
                ? long.MaxValue
                : Math.Max(txPoolConfig.MaxBlobTxSize.Value + (long)specProvider.GetFinalMaxBlobGasPerBlock(), ShortFormMaxContentLength);
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

        /// <summary>Decodes the wire-form transaction list, applying this serializer's configured pre-decode size caps.</summary>
        /// <remarks>
        /// An item whose pre-decode size exceeds the configured transaction cap (or the blob cap for a type-3
        /// item) is skipped rather than decoded, avoiding the RLP-decode cost of an attacker-sized item - see
        /// <see cref="IsOverSizeLimit"/> for the measure.
        /// </remarks>
        internal IOwnedReadOnlyList<Transaction> DeserializeTxsWithSizeGuard(ref RlpReader ctx)
        {
            int checkPosition = ctx.ReadSequenceLength() + ctx.Position;
            int length = ctx.PeekNumberOfItemsRemaining(checkPosition);
            ctx.GuardLimit(length, RlpLimit);

            ArrayPoolList<Transaction> result = new(length);
            try
            {
                for (int i = 0; i < length; i++)
                {
                    if (IsOverSizeLimit(ref ctx, _maxTxSize, _maxBlobTxSize))
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

            if (size <= maxTxSize && size <= maxBlobTxSize) return false;
            if (!isTyped) return size > maxTxSize;

            byte txType = ctx.Peek(prefixLength, 1)[0];
            return txType == (byte)TxType.Blob ? size > maxBlobTxSize : size > maxTxSize;
        }
    }
}
