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
    public sealed class ReceiptMessageDecoder(bool skipStateAndStatus = false, bool skipBloom = false) : RlpDecoder<TxReceipt>
    {
        [return: MaybeNull]
        protected override TxReceipt DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (ctx.TryConsumeNull(out LiteRlpReader rlp, out int position)) return null;

            TxReceipt txReceipt = new();
            if (!rlp.IsSequenceNext(position))
            {
                rlp.SkipLength(ref position);
                txReceipt.TxType = (TxType)rlp.Data[position++];
            }

            rlp.ReadSequenceLength(ref position, out int sequenceLength);
            int receiptEnd = position + sequenceLength;

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
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
                txReceipt.GasUsedTotal = rlp.DecodeULong(ref position);
            }

            // When skipBloom is true (slim receipt), bloom is absent from the stream — nothing to skip.
            if (!skipBloom)
            {
                txReceipt.Bloom = rlp.DecodeBloomNonNull(ref position);
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
            static void ThrowUnexpectedReceiptField()
                => throw new RlpException("Unexpected receipt field");
        }

        /// <summary>The receipt's content length, and the length of the inner sequence the encoder repeats:
        /// the per-frame receipts for a frame transaction, the logs for every other type.</summary>
        private (int Total, int Inner) GetContentLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return (0, 0);
            }

            if (item.TxType == TxType.FrameTx)
            {
                return (FrameReceiptRlp.GetPayloadLength(item, out int framesLength), framesLength);
            }

            int contentLength = 0;
            contentLength += Rlp.LengthOf(item.GasUsedTotal);
            if (!skipBloom)
                contentLength += Rlp.LengthOf(item.Bloom);

            int logsLength = GetLogsLength(item);
            contentLength += Rlp.LengthOfSequence(logsLength);

            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (!skipStateAndStatus)
            {
                contentLength += isEip658Receipts
                    ? Rlp.LengthOf(item.StatusCode)
                    : Rlp.LengthOf(item.PostTransactionState);
            }

            return (contentLength, logsLength);
        }

        private static int GetLogsLength(TxReceipt item)
        {
            int logsLength = 0;
            LogEntry[] logs = GetLogs(item);
            for (int i = 0; i < logs.Length; i++)
            {
                logsLength += Rlp.LengthOf(logs[i]);
            }

            return logsLength;
        }

        private static LogEntry[] GetLogs(TxReceipt item)
            => item.Logs ?? throw new RlpException("Receipt logs are null.");

        /// <summary>
        /// https://eips.ethereum.org/EIPS/eip-2718
        /// </summary>
        public override int GetLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList.Length;
            }

            (int Total, _) = GetContentLength(item, rlpBehaviors);
            int receiptPayloadLength = Rlp.LengthOfSequence(Total);

            bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
            int result = item.TxType != TxType.Legacy
                ? isForTxRoot
                    ? (1 + receiptPayloadLength)
                    : Rlp.LengthOfSequence(1 + receiptPayloadLength) // Rlp(TransactionType || TransactionPayload)
                : receiptPayloadLength;
            return result;
        }

        public byte[] EncodeNew(TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList.Bytes;
            }

            int length = GetLength(item, rlpBehaviors);
            byte[] bytes = new byte[length];
            RlpWriter writer = new(bytes);
            Encode(ref writer, item, rlpBehaviors);
            return bytes;
        }

        public override void Encode<TWriter>(ref TWriter writer, TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            (int totalContentLength, int innerLength) = GetContentLength(item, rlpBehaviors);
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
                FrameReceiptRlp.EncodePayload(ref writer, item, innerLength);
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
                writer.Encode(item.Bloom);

            writer.StartSequence(innerLength);
            LogEntry[] logs = GetLogs(item);
            LogEntryDecoder logEntryDecoder = LogEntryDecoder.Instance;
            for (int i = 0; i < logs.Length; i++)
            {
                logEntryDecoder.Encode(ref writer, logs[i]);
            }
        }
    }
}
