// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using CkzgLib;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
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

        /// <summary>The largest measure <see cref="IsOverSizeLimit"/> can read off an RLP short-form item.</summary>
        /// <remarks>
        /// <c>PeekPrefixAndContentLength</c> bounds-checks only the long forms, so the bytes an item declares
        /// past its prefix are safe to read only once its declared length rules the short forms out. Both caps
        /// below are clamped to this measure, which keeps every item that reaches the type-byte peek, or the
        /// unchecked <c>SkipItem</c>, in the bounds-checked long form however small a limit is configured. A
        /// short form carries at most 55 content bytes (Yellow Paper, Appendix B) and a sequence item is
        /// measured with its one-byte prefix included, so 56 is the largest either kind can present. That
        /// coincides with <c>RlpHelpers.SmallPrefixBarrier</c>, which is internal to
        /// <c>Nethermind.Serialization.Rlp</c> and not visible from here.
        /// </remarks>
        private const long ShortFormMaxItemMeasure = 56;

        private readonly long _maxTxSize;

        /// <remarks>
        /// <c>SizeTxFilter</c> measures a blob transaction by its consensus encoding alone, so the cap it
        /// configures has to be raised here by the mempool-form sidecar the wire carries alongside it - see
        /// <see cref="MaxBlobSidecarOverhead"/>.
        /// </remarks>
        private readonly long _maxBlobTxSize;

        // Cached once per instance: a lambda capturing `this` is not hoisted to a static delegate by the
        // compiler, so creating it inline on every Deserialize call would allocate a new delegate per message.
        private readonly DecodeRlpValue<TransactionsMessage> _deserializeTransactionsMessage;

        public TransactionsMessageSerializer(ITxPoolConfig? txPoolConfig = null, ISpecProvider? specProvider = null)
        {
            _maxTxSize = Math.Max(txPoolConfig?.MaxTxSize ?? long.MaxValue, ShortFormMaxItemMeasure);
            _maxBlobTxSize = txPoolConfig?.MaxBlobTxSize is null || specProvider is null
                ? long.MaxValue
                : Math.Max(txPoolConfig.MaxBlobTxSize.Value + MaxBlobSidecarOverhead(specProvider.GetFinalSpec()), ShortFormMaxItemMeasure);
            _deserializeTransactionsMessage = (ref RlpReader ctx) => new TransactionsMessage(DeserializeTxsWithSizeGuard(ref ctx));
        }

        /// <summary>An upper bound on the bytes a blob transaction's mempool-form sidecar adds to the consensus
        /// encoding <c>SizeTxFilter</c> measures, for the largest blob count <paramref name="finalSpec"/> allows.</summary>
        /// <remarks>
        /// Follows <c>BlobTxDecoder</c>'s mempool-form encoding: the type byte and the wrapper sequence enclosing
        /// the consensus encoding, the proof-version byte (EIP-7594; absent for <see cref="ProofVersion.V0"/>,
        /// counted regardless), and the blob, commitment and proof lists - one commitment per blob, and per blob
        /// either a single proof or, under EIP-7594, one proof per extended-blob cell. Every RLP prefix is taken
        /// at its widest, so the result over-estimates by a handful of bytes per list. That direction is the safe
        /// one: too loose a cap only spends a decode on a transaction the pool then rejects, while too tight a
        /// cap silently drops transactions the pool would have accepted.
        /// </remarks>
        private static long MaxBlobSidecarOverhead(IReleaseSpec finalSpec)
        {
            // An RLP prefix byte plus the widest length-of-length it can carry.
            const long widestPrefix = 1 + sizeof(int);
            const long versionByte = 1;

            long proofsPerBlob = finalSpec.BlobProofVersion is ProofVersion.V1 ? Ckzg.CellsPerExtBlob : 1;
            long perBlob = widestPrefix + Ckzg.BytesPerBlob
                           + 1 + Ckzg.BytesPerCommitment
                           + proofsPerBlob * (1 + Ckzg.BytesPerProof);

            // The type byte and the wrapper sequence, the version byte, then the three sidecar lists.
            return 1 + widestPrefix + versionByte + 3 * widestPrefix + (long)finalSpec.MaxBlobCount * perBlob;
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

        /// <summary>Whether the item at the reader's current position exceeds the configured size caps.</summary>
        /// <remarks>
        /// Mirrors <c>SizeTxFilter</c>'s post-decode measure (<c>tx.GetLength(shouldCountBlobs: false)</c>): a
        /// legacy item's comparable length includes its RLP prefix, but a typed item's mempool-form wrapper
        /// prefix is not part of that measure, so only its content length is compared. For a non-blob item the
        /// two measures agree exactly. A blob item is measured on the wire with the sidecar that filter ignores
        /// altogether, which the blob cap covers by <see cref="MaxBlobSidecarOverhead"/> - so a blob transaction
        /// the pool would accept is never skipped here as long as it carries no more than the final spec's
        /// <c>MaxBlobCount</c> blobs with the proofs that spec's version prescribes. One exceeding those bounds
        /// may be skipped; the pool would reject it on validation regardless.
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
