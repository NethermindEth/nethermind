// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

#pragma warning disable 618

namespace Nethermind.Serialization.Rlp
{
    // EIP-8141: frame receipts append [payer, [frame_receipt, ...]] after the standard storage fields. Only
    // TxType.FrameTx receipts carry the extension, so pre-fork data round-trips unchanged.
    [Rlp.Decoder(RlpDecoderKey.LegacyStorage)]
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ReceiptStorageDecoder))]
    public sealed class ReceiptStorageDecoder(bool supportTxHash = true) : RlpDecoder<TxReceipt>, IReceiptRefDecoder
    {
        private const byte MarkTxHashByte = 255;
        private static readonly KeccakDecoder HashDecoder = KeccakDecoder.Instance;

        // Used by Rlp decoders discovery
        public ReceiptStorageDecoder() : this(true)
        {
        }

        protected override TxReceipt? DecodeInternal(ref RlpReader reader,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
                reader.ReadRaw(1);
                txReceipt.TxType = (TxType)first;
                DecodeReceipt<TCanonicalChecks>(ref reader, txReceipt, rlpBehaviors);
            }
            else
            {
                RlpReader payload = ReadEnvelope<TCanonicalChecks>(ref reader, out TxType txType);
                txReceipt.TxType = txType;
                DecodeReceipt<TCanonicalChecks>(ref payload, txReceipt, rlpBehaviors);
                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
                {
                    payload.CheckEnd();
                }
            }

            return txReceipt;
        }

        /// <summary>Reads the byte string around a typed receipt.</summary>
        /// <returns>A reader at the receipt sequence, after the type byte.</returns>
        private static RlpReader ReadEnvelope<TCanonicalChecks>(scoped ref RlpReader reader, out TxType txType) where TCanonicalChecks : struct, IFlag
        {
            ReadOnlySpan<byte> envelope = reader.ReadBytes<TCanonicalChecks>();
            if (envelope.IsEmpty) ThrowEmptyEnvelope();

            txType = (TxType)envelope[0];
            return new RlpReader(envelope[1..]);
        }

        private void DecodeReceipt<TCanonicalChecks>(ref RlpReader reader, TxReceipt txReceipt, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
        {
            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;
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

            if (isStorage)
            {
                txReceipt.BlockHash = fields.DecodeKeccakOrNull();
                txReceipt.BlockNumber = fields.DecodeULong<TCanonicalChecks>();
                txReceipt.Index = fields.DecodePositiveInt<TCanonicalChecks>();
                txReceipt.Sender = fields.DecodeAddressOrNull();
                txReceipt.Recipient = fields.DecodeAddressOrNull();
                txReceipt.ContractAddress = fields.DecodeAddressOrNull();
                txReceipt.GasUsed = fields.DecodeULong<TCanonicalChecks>();
            }

            txReceipt.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();
            txReceipt.Bloom = fields.DecodeBloomOrNull();

            RlpReader logs = fields.EnterSequence<TCanonicalChecks>(out int logsLength);
            LogEntry[] logEntries = DecodeStoredLogs<TCanonicalChecks>(ref logs);
            fields.ExitSequence(in logs, logsLength);

            if (fields.HasNext)
            {
                DecodeTrailingFields<TCanonicalChecks>(ref fields, txReceipt, rlpBehaviors);
            }

            reader.ExitSequence(in fields, receiptLength, rlpBehaviors);
            txReceipt.Logs = logEntries;
        }

        /// <summary>Decodes the fields that later versions added after the logs.</summary>
        // Not inlined, so that its string read does not make the frame of the receipt decode larger.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DecodeTrailingFields<TCanonicalChecks>(ref RlpReader fields, TxReceipt txReceipt, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
        {
            // since txHash was added later and may not be in rlp, we provide special mark byte that it will be next
            if ((rlpBehaviors & RlpBehaviors.Storage) != 0 && supportTxHash && fields.PeekByte() == MarkTxHashByte)
            {
                fields.ReadRaw(1);
                txReceipt.TxHash = fields.DecodeKeccakOrNull();
            }

            // since error was added later we can only rely on it in cases where we read receipt only and no data follows, empty errors might not be serialized
            if (fields.HasNext)
            {
                txReceipt.Error = fields.DecodeString();
            }

            if (txReceipt.TxType == TxType.FrameTx && fields.HasNext)
            {
                // Null-tolerant like every other address here: an earlier build wrote a payer-less receipt,
                // and refusing to parse it would take the whole block's receipt array with it.
                txReceipt.Payer = fields.DecodeAddressOrNull();
                txReceipt.FrameReceipts = FrameReceiptRlp.DecodeStoredFrames<TCanonicalChecks>(ref fields, LogEntryDecoder.Instance, rlpBehaviors);
            }
        }

        /// <summary>Decodes the logs of a stored receipt, and leaves out the null ones.</summary>
        private static LogEntry[] DecodeStoredLogs<TCanonicalChecks>(ref RlpReader logs) where TCanonicalChecks : struct, IFlag
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
                LogEntry? logEntry = LogEntryDecoder.Decode<TCanonicalChecks>(ref logs);
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

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowEmptyEnvelope()
            => throw new RlpReaderException("Typed receipt envelope is empty");

        public override void Encode(ref RlpWriter writer, TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);
            int sequenceLength = Rlp.LengthOfSequence(totalContentLength);

            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;
            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (item.TxType != TxType.Legacy)
            {
                if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.None)
                {
                    writer.StartByteArray(sequenceLength + 1, false);
                }

                writer.WriteByte((byte)item.TxType);
            }

            RlpSequence sequence = writer.StartSequence(totalContentLength);
            if (isEip658receipts)
            {
                writer.Encode(item.StatusCode);
            }
            else
            {
                writer.Encode(item.PostTransactionState);
            }

            if (isStorage)
            {
                writer.Encode(item.BlockHash);
                writer.Encode(item.BlockNumber);
                writer.Encode(item.Index);
                writer.Encode(item.Sender);
                writer.Encode(item.Recipient);
                writer.Encode(item.ContractAddress);
                writer.Encode(item.GasUsed);
                writer.Encode(item.GasUsedTotal);
                writer.Encode(item.Bloom);

                EncodeLogs(ref writer, item, logsLength);

                if (supportTxHash)
                {
                    writer.WriteByte(MarkTxHashByte);
                    writer.Encode(item.TxHash);
                }

                writer.Encode(item.Error);
            }
            else
            {
                writer.Encode(item.GasUsedTotal);
                writer.Encode(item.Bloom);

                EncodeLogs(ref writer, item, logsLength);

                writer.Encode(item.Error);
            }

            if (item.TxType == TxType.FrameTx)
            {
                // Repeats the logs the top-level union already holds: DecodeStructRef hands eth_getLogs one
                // contiguous LogsRlp span, which N per-frame sequences cannot supply.
                FrameReceiptRlp.EncodeStoredExtension(ref writer, item, LogEntryDecoder.Instance);
            }

            writer.EndSequence(sequence);
        }

        private static void EncodeLogs(ref RlpWriter writer, TxReceipt item, int logsLength)
        {
            RlpSequence logsSequence = writer.StartSequence(logsLength);

            LogEntry[] logs = GetLogs(item);
            for (int i = 0; i < logs.Length; i++)
            {
                LogEntryDecoder.Instance.Encode(ref writer, logs[i]);
            }

            writer.EndSequence(logsSequence);
        }

        private (int Total, int Logs) GetContentLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
        {
            int contentLength = 0;
            if (item is null)
            {
                return (contentLength, 0);
            }

            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;

            if (isStorage)
            {
                contentLength += Rlp.LengthOf(item.BlockHash);
                contentLength += Rlp.LengthOf(item.BlockNumber);
                contentLength += Rlp.LengthOf(item.Index);
                contentLength += Rlp.LengthOf(item.Sender);
                contentLength += Rlp.LengthOf(item.Recipient);
                contentLength += Rlp.LengthOf(item.ContractAddress);
                contentLength += Rlp.LengthOf(item.GasUsed);
                if (supportTxHash) contentLength += 1 + Rlp.LengthOf(item.TxHash);
            }

            contentLength += Rlp.LengthOf(item.GasUsedTotal);
            contentLength += Rlp.LengthOf(item.Bloom);

            int logsLength = GetLogsLength(item);
            contentLength += Rlp.LengthOfSequence(logsLength);

            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (isEip658Receipts)
            {
                contentLength += Rlp.LengthOf(item.StatusCode);
            }
            else
            {
                contentLength += Rlp.LengthOf(item.PostTransactionState);
            }

            contentLength += Rlp.LengthOf(item.Error);

            if (item.TxType == TxType.FrameTx)
            {
                contentLength += FrameReceiptRlp.GetStoredExtensionLength(item, LogEntryDecoder.Instance);
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

            (int Total, _) = GetContentLength(item, rlpBehaviors);
            int receiptPayloadLength = Rlp.LengthOfSequence(Total);

            bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
            int result = item.TxType != TxType.Legacy
                ? isForTxRoot
                    ? (1 + receiptPayloadLength)
                    : Rlp.LengthOfSequence(1 + receiptPayloadLength) // Rlp(TransactionType || ReceiptPayload)
                : receiptPayloadLength;
            return result;
        }

        public void DecodeStructRef(scoped ref RlpReader decoderContext, RlpBehaviors rlpBehaviors,
            out TxReceiptStructRef item)
        {
            if ((rlpBehaviors & RlpBehaviors.TrustedInput) != 0)
            {
                DecodeStructRef<OffFlag>(ref decoderContext, rlpBehaviors, out item);
            }
            else
            {
                DecodeStructRef<OnFlag>(ref decoderContext, rlpBehaviors, out item);
            }
        }

        private void DecodeStructRef<TCanonicalChecks>(scoped ref RlpReader decoderContext, RlpBehaviors rlpBehaviors,
            out TxReceiptStructRef item) where TCanonicalChecks : struct, IFlag
        {
            if (decoderContext.TryReadEmptySequence())
            {
                item = new TxReceiptStructRef();
                return;
            }

            byte first = decoderContext.PeekByte();
            if (first >= Rlp.EmptyListByte)
            {
                DecodeStructRefReceipt<TCanonicalChecks>(ref decoderContext, TxType.Legacy, rlpBehaviors, out item);
            }
            else if (first < Rlp.EmptyByteArrayByte)
            {
                decoderContext.ReadRaw(1);
                DecodeStructRefReceipt<TCanonicalChecks>(ref decoderContext, (TxType)first, rlpBehaviors, out item);
            }
            else
            {
                RlpReader payload = ReadEnvelope<TCanonicalChecks>(ref decoderContext, out TxType txType);
                DecodeStructRefReceipt<TCanonicalChecks>(ref payload, txType, rlpBehaviors, out item);
            }
        }

        private void DecodeStructRefReceipt<TCanonicalChecks>(scoped ref RlpReader reader, TxType txType, RlpBehaviors rlpBehaviors,
            out TxReceiptStructRef item) where TCanonicalChecks : struct, IFlag
        {
            item = new TxReceiptStructRef { TxType = txType };
            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;
            RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int receiptLength);
            ReadOnlySpan<byte> firstItem = fields.DecodeByteArraySpan<TCanonicalChecks>();
            if (firstItem.Length == 1)
            {
                item.StatusCode = firstItem[0];
            }
            else
            {
                item.PostTransactionState =
                    firstItem.Length == 0 ? new Hash256StructRef() : new Hash256StructRef(firstItem);
            }

            if (isStorage)
            {
                fields.DecodeKeccakStructRef(out item.BlockHash);
                item.BlockNumber = fields.DecodeULong<TCanonicalChecks>();
                item.Index = fields.DecodePositiveInt<TCanonicalChecks>();
                fields.DecodeAddressStructRef(out item.Sender);
                fields.DecodeAddressStructRef(out item.Recipient);
                fields.DecodeAddressStructRef(out item.ContractAddress);
                item.GasUsed = fields.DecodeULong<TCanonicalChecks>();
            }
            item.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();
            fields.DecodeBloomStructRef(out item.Bloom, out bool bloomWasMissing);
            if (bloomWasMissing)
            {
                item.Bloom = default;
            }

            item.LogsRlp = fields.ReadEncodedItem<TCanonicalChecks>();

            if (isStorage && supportTxHash && fields.HasNext)
            {
                // since txHash was added later and may not be in rlp, we provide special mark byte that it will be next
                if (fields.PeekByte() == MarkTxHashByte)
                {
                    fields.ReadRaw(1);
                    fields.DecodeKeccakStructRef(out item.TxHash);
                }
            }

            // since error was added later we can only rely on it in cases where we read receipt only and no data follows, empty errors might not be serialized
            if (fields.HasNext)
            {
                item.Error = fields.DecodeString();
            }

            // EIP-8141: move to the receipt's end so the next receipt in an array stays aligned; for a frame tx
            // this skips the trailing [payer, per-frame receipts].
            if (fields.HasNext)
            {
                reader.ExitSequenceIgnoringRest(in fields, receiptLength);
            }
            else
            {
                reader.ExitSequence(in fields, receiptLength);
            }
        }

        public void DecodeLogEntryStructRef(scoped ref RlpReader decoderContext, RlpBehaviors behaviour,
            out LogEntryStructRef current) => LogEntryDecoder.DecodeStructRef(ref decoderContext, behaviour, out current);

        public Hash256[] DecodeTopics(RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => HashDecoder.DecodeNonNullArray(ref reader, rlpBehaviors);

        public bool CanDecodeBloom => true;
    }
}
