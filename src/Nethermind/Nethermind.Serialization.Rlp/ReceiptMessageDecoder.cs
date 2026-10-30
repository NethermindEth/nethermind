// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp
{
    [Rlp.Decoder(RlpDecoderKey.Default)]
    [Rlp.Decoder(RlpDecoderKey.Trie)]
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ReceiptMessageDecoder))]
    public sealed partial class ReceiptMessageDecoder(bool skipStateAndStatus = false, bool skipBloom = false) : RlpDecoder<TxReceipt>
    {
        [return: MaybeNull]
        protected override TxReceipt DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

        private TxReceipt? Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
        {
            if (reader.TryReadEmptySequence()) return null;

            TxReceipt txReceipt = new();
            byte first = reader.PeekByte();
            if (first >= Rlp.EmptyListByte)
            {
                DecodeReceipt<TCanonicalChecks>(ref reader, txReceipt, rlpBehaviors);
            }
            else if (first < Rlp.EmptyByteArrayByte)
            {
                // The type byte without the byte string around it, as the receipt trie holds it.
                reader.ReadRaw(1);
                txReceipt.TxType = (TxType)first;
                DecodeReceipt<TCanonicalChecks>(ref reader, txReceipt, rlpBehaviors);
            }
            else
            {
                ReadOnlySpan<byte> envelope = reader.ReadBytes<TCanonicalChecks>();
                if (envelope.IsEmpty) ThrowEmptyEnvelope();

                txReceipt.TxType = (TxType)envelope[0];
                RlpReader payload = new(envelope[1..]);
                DecodeReceipt<TCanonicalChecks>(ref payload, txReceipt, rlpBehaviors);
                payload.CheckEnd();
            }

            return txReceipt;
        }

        private void DecodeReceipt<TCanonicalChecks>(ref RlpReader reader, TxReceipt txReceipt, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
        {
            RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int receiptLength);
            if (txReceipt.TxType == TxType.FrameTx)
            {
                FrameReceiptRlp.DecodePayload<TCanonicalChecks>(ref fields, txReceipt);
                reader.ExitSequence(in fields, receiptLength, rlpBehaviors);
                return;
            }

            ReadOnlySpan<byte> firstItem = fields.ReadBytes<TCanonicalChecks>();
            if (firstItem.Length == 1 && (firstItem[0] == 0 || firstItem[0] == 1))
            {
                txReceipt.StatusCode = firstItem[0];
                txReceipt.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();
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
                txReceipt.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();
            }

            // When skipBloom is true (slim receipt), bloom is absent from the stream — nothing to skip.
            if (!skipBloom)
            {
                txReceipt.Bloom = fields.DecodeBloomNonNull(rlpBehaviors);
            }

            RlpReader logs = fields.EnterSequence<TCanonicalChecks>(out int logsLength);
            txReceipt.Logs = LogEntryDecoder.DecodeLogs<TCanonicalChecks>(ref logs);
            fields.ExitSequence(in logs, logsLength);

            reader.ExitSequence(in fields, receiptLength, rlpBehaviors);
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowUnexpectedPostStateLength(int length)
            => throw new RlpReaderException($"Unexpected post-transaction state length {length}; expected 0 or {Hash256.Size} bytes");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowEmptyEnvelope()
            => throw new RlpReaderException("Typed receipt envelope is empty");

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
                contentLength += Rlp.LengthOf(GetBloom(item, rlpBehaviors));

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

        private static Bloom GetBloom(TxReceipt item, RlpBehaviors rlpBehaviors)
            => (rlpBehaviors & RlpBehaviors.Eip7668Receipts) != 0 ? Bloom.ZeroLength : item.Bloom;

        private static LogEntry[] GetLogs(TxReceipt item)
            => item.Logs ?? throw new RlpReaderException("Receipt logs are null.");

        /// <summary>
        /// https://eips.ethereum.org/EIPS/eip-2718
        /// </summary>
        public override int GetLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList.Length;
            }

            int length = -1;
            if (!skipStateAndStatus && !skipBloom) GetLengthInOnePass(item, rlpBehaviors, ref length);
            if (length >= 0) return length;

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
            RlpWriter writer = RlpWriter.ContentWriter(bytes);
            Encode(ref writer, item, rlpBehaviors);
            Rlp.CheckLength(in writer, length);
            return bytes;
        }

        partial void GetLengthInOnePass(TxReceipt item, RlpBehaviors rlpBehaviors, ref int length);

        partial void EncodeInOnePass(ref RlpWriter writer, TxReceipt item, RlpBehaviors rlpBehaviors, ref bool encoded);

        public override void Encode(ref RlpWriter writer, TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            bool encoded = false;
            if (!skipStateAndStatus && !skipBloom) EncodeInOnePass(ref writer, item, rlpBehaviors, ref encoded);
            if (encoded) return;

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

            RlpSequence sequence = writer.StartSequence(totalContentLength);

            if (item.TxType == TxType.FrameTx)
            {
                FrameReceiptRlp.EncodePayload(ref writer, item, innerLength);
                writer.EndSequence(sequence);
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

            RlpSequence logsSequence = writer.StartSequence(innerLength);
            LogEntry[] logs = GetLogs(item);
            LogEntryDecoder logEntryDecoder = LogEntryDecoder.Instance;
            for (int i = 0; i < logs.Length; i++)
            {
                logEntryDecoder.Encode(ref writer, logs[i]);
            }

            writer.EndSequence(logsSequence);
            writer.EndSequence(sequence);
        }
    }
}
