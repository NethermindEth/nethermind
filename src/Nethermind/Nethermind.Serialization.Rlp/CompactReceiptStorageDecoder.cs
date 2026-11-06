// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using static Nethermind.Serialization.Rlp.Rlp;

#pragma warning disable 618

namespace Nethermind.Serialization.Rlp
{
    [Decoder(RlpDecoderKey.Storage)]
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CompactReceiptStorageDecoder))]
    public sealed class CompactReceiptStorageDecoder() : RlpDecoder<TxReceipt>, IReceiptRefTopicDecoder
    {
        public static readonly CompactReceiptStorageDecoder Instance = new();

        protected override TxReceipt? DecodeInternal(ref RlpReader reader,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

        internal static TxReceipt? Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
        {
            if (reader.TryReadEmptySequence()) return null;

            TxReceipt txReceipt = new();
            RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int receiptLength);

            ReadOnlySpan<byte> firstItem = fields.ReadBytes<TCanonicalChecks>();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                if (firstItem.Length != 0 && firstItem.Length != Hash256.Size)
                {
                    ThrowUnexpectedPostStateLength(firstItem.Length);
                }

                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
            }

            txReceipt.Sender = fields.DecodeAddressOrNull();
            txReceipt.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();

            RlpReader logs = fields.EnterSequence<TCanonicalChecks>(out int logsLength);
            txReceipt.Logs = DecodeLogs<TCanonicalChecks>(ref logs);
            fields.ExitSequence(in logs, logsLength);

            // EIP-8141: only frame receipts carry data past the logs sequence — a trailing extension holding the
            // payer and per-frame receipts. Pre-fork receipts end at the logs, so the branch never fires for them.
            if (fields.HasNext)
            {
                txReceipt.TxType = TxType.FrameTx;
                // Null-tolerant like the sender above: an earlier build wrote a payer-less receipt, and
                // refusing to parse it would take the whole block's receipt array with it.
                txReceipt.Payer = fields.DecodeAddressOrNull();
                txReceipt.FrameReceipts = FrameReceiptRlp.DecodeStoredFrames<TCanonicalChecks>(ref fields, CompactLogEntryDecoder.Instance, rlpBehaviors);
            }

            reader.ExitSequence(in fields, receiptLength, rlpBehaviors);

            // EIP-7668: the bloom would be discarded. Otherwise TxReceipt computes it from the logs on first read.
            if ((rlpBehaviors & RlpBehaviors.Eip7668Receipts) != 0)
            {
                txReceipt.Bloom = Bloom.ZeroLength;
            }

            return txReceipt;
        }

        /// <summary>Decodes the logs of a stored receipt, and leaves out the null ones.</summary>
        private static LogEntry[] DecodeLogs<TCanonicalChecks>(ref RlpReader logs) where TCanonicalChecks : struct, IFlag
        {
            // Each log takes at least one byte, so the count cannot reach the bound.
            int count = logs.CountItems<TCanonicalChecks>(logs.Remaining + 1);
            if (count == 0) return [];

            LogEntry[] logEntries = new LogEntry[count];
            // A span store has no covariance check, unlike an array store of a reference type.
            Span<LogEntry> span = logEntries;
            int decoded = 0;
            for (int i = 0; i < span.Length; i++)
            {
                LogEntry? logEntry = CompactLogEntryDecoder.Decode<TCanonicalChecks>(ref logs);
                if (logEntry is not null)
                {
                    span[decoded++] = logEntry;
                }
            }

            if (decoded != count) Array.Resize(ref logEntries, decoded);
            return logEntries;
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowUnexpectedPostStateLength(int length)
            => throw new RlpReaderException($"Unexpected post-transaction state length {length}; expected 0 or {Hash256.Size} bytes");

        public void DecodeStructRef(scoped ref RlpReader decoderContext, RlpBehaviors rlpBehaviors,
            out TxReceiptStructRef item)
        {
            if ((rlpBehaviors & RlpBehaviors.TrustedInput) != 0)
            {
                DecodeStructRef<OffFlag>(ref decoderContext, out item);
            }
            else
            {
                DecodeStructRef<OnFlag>(ref decoderContext, out item);
            }
        }

        private static void DecodeStructRef<TCanonicalChecks>(scoped ref RlpReader decoderContext, out TxReceiptStructRef item)
            where TCanonicalChecks : struct, IFlag
        {
            // Note: This method runs at 2.5 million times/sec on my machine
            item = new TxReceiptStructRef();

            if (decoderContext.TryReadEmptySequence())
            {
                return;
            }

            RlpReader fields = decoderContext.EnterSequence<TCanonicalChecks>(out int receiptLength);

            ReadOnlySpan<byte> firstItem = fields.DecodeByteArraySpan<TCanonicalChecks>(RlpLimit.L32);
            if (firstItem.Length == 1)
            {
                item.StatusCode = firstItem[0];
            }
            else
            {
                item.PostTransactionState =
                    firstItem.Length == 0 ? new Hash256StructRef() : new Hash256StructRef(firstItem);
            }

            fields.DecodeAddressStructRef(out item.Sender);
            item.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();
            item.LogsRlp = fields.ReadEncodedItem<TCanonicalChecks>();

            // EIP-8141: skip a frame-tx receipt's trailing extension so the next receipt in the array stays aligned.
            if (fields.HasNext)
            {
                item.TxType = TxType.FrameTx;
                decoderContext.ExitSequenceIgnoringRest(in fields, receiptLength);
            }
            else
            {
                decoderContext.ExitSequence(in fields, receiptLength);
            }
        }

        public void DecodeLogEntryStructRef(scoped ref RlpReader decoderContext, RlpBehaviors none,
            out LogEntryStructRef current) => CompactLogEntryDecoder.DecodeLogEntryStructRef(ref decoderContext, none, out current);

        public Hash256[] DecodeTopics(RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => CompactLogEntryDecoder.DecodeTopics(reader, rlpBehaviors);

        public RlpReader StartDecodingTopics(scoped ref RlpReader reader) => CompactLogEntryDecoder.StartDecodingTopics(ref reader);

        public void DecodeTopic(ref RlpReader reader, int index, scoped Span<byte> topic) => CompactLogEntryDecoder.DecodeTopic(ref reader, topic);

        // Refstruct decode does not generate bloom
        public bool CanDecodeBloom => false;

        public override void Encode(ref RlpWriter writer, TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);

            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            // Note: Any byte saved here is about 3GB on mainnet.
            RlpSequence sequence = writer.StartSequence(totalContentLength);
            if (isEip658receipts)
            {
                writer.Encode(item.StatusCode);
            }
            else
            {
                writer.Encode(item.PostTransactionState);
            }

            writer.Encode(item.Sender);
            writer.Encode(item.GasUsedTotal);

            RlpSequence logsSequence = writer.StartSequence(logsLength);

            LogEntry[] logs = GetLogs(item);
            for (int i = 0; i < logs.Length; i++)
            {
                CompactLogEntryDecoder.Instance.Encode(ref writer, logs[i]);
            }

            writer.EndSequence(logsSequence);

            if (item.TxType == TxType.FrameTx)
            {
                // Repeats the logs the top-level union already holds, at the cost noted above: DecodeStructRef
                // hands eth_getLogs one contiguous LogsRlp span, which N per-frame sequences cannot supply.
                FrameReceiptRlp.EncodeStoredExtension(ref writer, item, CompactLogEntryDecoder.Instance);
            }

            writer.EndSequence(sequence);
        }

        private static (int Total, int Logs) GetContentLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
        {
            int contentLength = 0;
            if (item is null)
            {
                return (contentLength, 0);
            }

            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;
            if (isEip658Receipts)
            {
                contentLength += Rlp.LengthOf(item.StatusCode);
            }
            else
            {
                contentLength += Rlp.LengthOf(item.PostTransactionState);
            }

            contentLength += Rlp.LengthOf(item.Sender);
            contentLength += Rlp.LengthOf(item.GasUsedTotal);

            int logsLength = GetLogsLength(item);
            contentLength += Rlp.LengthOfSequence(logsLength);

            if (item.TxType == TxType.FrameTx)
            {
                contentLength += FrameReceiptRlp.GetStoredExtensionLength(item, CompactLogEntryDecoder.Instance);
            }

            return (contentLength, logsLength);
        }

        private static int GetLogsLength(TxReceipt item)
        {
            int logsLength = 0;
            LogEntry[] logs = GetLogs(item);
            for (int i = 0; i < logs.Length; i++)
            {
                logsLength += CompactLogEntryDecoder.Instance.GetLength(logs[i]);
            }

            return logsLength;
        }

        private static LogEntry[] GetLogs(TxReceipt item)
            => item.Logs ?? throw new RlpReaderException("Receipt logs are null.");

        public override int GetLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
        {
            (int Total, _) = GetContentLength(item, rlpBehaviors);
            return Rlp.LengthOfSequence(Total);
        }
    }
}
