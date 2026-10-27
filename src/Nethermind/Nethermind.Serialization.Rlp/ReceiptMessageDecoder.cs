// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp
{
    [Rlp.Decoder(RlpDecoderKey.Default)]
    [Rlp.Decoder(RlpDecoderKey.Trie)]
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ReceiptMessageDecoder))]
    public sealed partial class ReceiptMessageDecoder(bool skipStateAndStatus = false, bool skipBloom = false) : RlpDecoder<TxReceipt>
    {
        [return: MaybeNull]
        protected override TxReceipt DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (ctx.TryConsumeNull(out LiteRlpReader rlp, out int position)) return null;

            TxReceipt txReceipt = new();
            int wrapperEnd = -1;
            if (!rlp.IsSequenceNext(position))
            {
                (int prefixLength, int contentLength) = rlp.PeekPrefixAndContentLength(position);
                position += prefixLength;
                if (prefixLength != 0) wrapperEnd = position + contentLength;
                txReceipt.TxType = (TxType)rlp.Data[position++];
            }

            rlp.ReadSequenceLength(ref position, out int sequenceLength);
            int receiptEnd = position + sequenceLength;
            if (wrapperEnd >= 0) RlpHelpers.Check(receiptEnd, wrapperEnd);

            if (txReceipt.TxType == TxType.FrameTx)
            {
                ctx.Position = position;
                FrameReceiptRlp.DecodePayload(ref ctx, txReceipt, receiptEnd, rlpBehaviors);
                return txReceipt;
            }

            rlp.DecodeByteArray(ref position, out byte[] firstItem);
            if (firstItem.Length == 1 && (firstItem[0] == 0 || firstItem[0] == 1))
            {
                txReceipt.StatusCode = firstItem[0];
                txReceipt.GasUsedTotal = rlp.DecodeULong(ref position);
            }
            else if (firstItem.Length is >= 1 and <= 4)
            {
                txReceipt.GasUsedTotal = firstItem.ToULong();
            }
            else
            {
                if (firstItem.Length != 0 && firstItem.Length != Hash256.Size)
                {
                    ThrowUnexpectedPostStateLength(firstItem.Length);
                }

                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
                txReceipt.GasUsedTotal = rlp.DecodeULong(ref position);
            }

            // When skipBloom is true (slim receipt), bloom is absent from the stream — nothing to skip.
            if (!skipBloom)
            {
                txReceipt.Bloom = rlp.DecodeBloomNonNull(ref position, rlpBehaviors);
            }

            rlp.ReadSequenceLength(ref position, out int logsLength);
            int lastCheck = position + logsLength;

            ctx.Position = position;
            txReceipt.Logs = LogEntryDecoder.DecodeLogs(ref ctx, lastCheck);

            // The item count only requires a log to start before the declared end, so an under-declared
            // logs header is only caught here; the logs are last, so the receipt end lands on it.
            ctx.Check(lastCheck);

            // Handle any remaining extra bytes
            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (ctx.Position != receiptEnd)
            {
                if (allowExtraBytes)
                {
                    ctx.Position = receiptEnd;
                }
                else
                {
                    ThrowUnexpectedReceiptField();
                }
            }

            return txReceipt;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnexpectedPostStateLength(int length)
                => throw new RlpException($"Unexpected post-transaction state length {length}; expected 0 or {Hash256.Size} bytes");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnexpectedReceiptField()
                => throw new RlpException("Unexpected receipt field");
        }

        /// <summary>Populates the receipt and returns its content length.</summary>
        private int PopulateContentLength(TxReceipt item, ref LengthStack lengths, RlpBehaviors rlpBehaviors)
        {
            if (item.TxType == TxType.FrameTx)
            {
                return FrameReceiptRlp.PopulatePayloadLength(item, ref lengths);
            }

            int contentLength = LogEntryDecoder.Instance.PopulateSequenceAndPeek(GetLogs(item), ref lengths);
            contentLength += Rlp.LengthOf(item.GasUsedTotal);
            if (!skipBloom)
                contentLength += Rlp.LengthOf(GetBloom(item, rlpBehaviors));

            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (!skipStateAndStatus)
            {
                contentLength += isEip658Receipts
                    ? Rlp.LengthOf(item.StatusCode)
                    : Rlp.LengthOf(item.PostTransactionState);
            }

            return contentLength;
        }

        private static Bloom GetBloom(TxReceipt item, RlpBehaviors rlpBehaviors)
            => (rlpBehaviors & RlpBehaviors.Eip7668Receipts) != 0 ? Bloom.ZeroLength : item.Bloom;

        private static LogEntry[] GetLogs(TxReceipt item)
            => item.Logs ?? throw new RlpException("Receipt logs are null.");

        /// <summary>
        /// https://eips.ethereum.org/EIPS/eip-2718
        /// </summary>
        public override void PopulateLength(TxReceipt? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                lengths.Push(Rlp.OfEmptyList.Length);
                return;
            }

            int contentLength = PopulateContentLength(item, ref lengths, rlpBehaviors);
            int receiptPayloadLength = Rlp.LengthOfSequence(contentLength);

            bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
            lengths.Push(contentLength);
            lengths.Push(item.TxType != TxType.Legacy
                ? isForTxRoot
                    ? (1 + receiptPayloadLength)
                    : Rlp.LengthOfSequence(1 + receiptPayloadLength) // Rlp(TransactionType || TransactionPayload)
                : receiptPayloadLength);
        }

        public byte[] EncodeNew(TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            item is null ? Rlp.OfEmptyList.Bytes : EncodeAsBytes(item, rlpBehaviors);

        partial void EncodeInOnePass<TWriter>(TxReceipt item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors, ref bool encoded)
            where TWriter : struct, IRlpWriteBackend, allows ref struct;

        public override void Encode<TWriter>(TxReceipt? item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                lengths.Pop();
                writer.EncodeNullObject();
                return;
            }

            bool encoded = false;
            if (!skipStateAndStatus && !skipBloom) EncodeInOnePass(item, ref lengths, ref writer, rlpBehaviors, ref encoded);
            if (encoded) return;

            lengths.Pop();
            int totalContentLength = lengths.Pop();
            int sequenceLength = Rlp.LengthOfSequence(totalContentLength);

            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (item.TxType != TxType.Legacy)
            {
                if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.None)
                {
                    writer.StartByteArray(sequenceLength + 1, false);
                }

                writer.WriteByte((byte)item.TxType);
            }

            writer.StartSequence(totalContentLength);

            if (item.TxType == TxType.FrameTx)
            {
                FrameReceiptRlp.EncodePayload(ref writer, item, ref lengths);
                return;
            }

            if (!skipStateAndStatus)
            {
                if (isEip658Receipts)
                {
                    writer.Encode(item.StatusCode);
                }
                else
                {
                    writer.Encode(item.PostTransactionState);
                }
            }

            writer.Encode(item.GasUsedTotal);
            if (!skipBloom)
                writer.Encode(GetBloom(item, rlpBehaviors));

            LogEntryDecoder.Instance.EncodeSequence(GetLogs(item), ref lengths, ref writer);
        }
    }
}
