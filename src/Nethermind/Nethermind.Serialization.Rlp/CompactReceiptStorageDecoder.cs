// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using static Nethermind.Serialization.Rlp.Rlp;

#pragma warning disable 618

namespace Nethermind.Serialization.Rlp
{
    [Decoder(RlpDecoderKey.Storage)]
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CompactReceiptStorageDecoder))]
    public sealed class CompactReceiptStorageDecoder() : RlpDecoder<TxReceipt>, IReceiptRefDecoder
    {
        public static readonly CompactReceiptStorageDecoder Instance = new();

        protected override TxReceipt? DecodeInternal(ref RlpReader decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.TryConsumeNull(out LiteRlpReader rlp, out int position)) return null;

            TxReceipt txReceipt = new();
            rlp.ReadSequenceLength(ref position, out int receiptLength);
            int receiptEnd = position + receiptLength;

            rlp.DecodeByteArray(ref position, out byte[] firstItem);
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

            txReceipt.Sender = rlp.DecodeAddressOrNull(ref position);
            txReceipt.GasUsedTotal = rlp.DecodeULong(ref position);

            rlp.ReadSequenceLength(ref position, out int sequenceLength);
            int lastCheck = position + sequenceLength;
            decoderContext.Position = position;

            // Don't know the size exactly, I'll just assume its just an address and add some margin
            using ArrayPoolListRef<LogEntry> logEntries = new(sequenceLength * 2 / Rlp.LengthOfAddressRlp);
            while (decoderContext.Position < lastCheck)
            {
                LogEntry? logEntry = CompactLogEntryDecoder.Instance.Decode(
                    ref decoderContext,
                    RlpBehaviors.AllowExtraBytes);
                if (logEntry is not null)
                {
                    logEntries.Add(logEntry);
                }
            }

            txReceipt.Logs = logEntries.ToArray();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                RlpHelpers.Check(decoderContext.Position, lastCheck);
            }

            // EIP-8141: only frame receipts carry data past the logs sequence — a trailing extension holding the
            // payer and per-frame receipts. Pre-fork receipts end at the logs, so the branch never fires for them.
            if (decoderContext.Position < receiptEnd)
            {
                txReceipt.TxType = TxType.FrameTx;
                // Null-tolerant like the sender above: an earlier build wrote a payer-less receipt, and
                // refusing to parse it would take the whole block's receipt array with it.
                txReceipt.Payer = decoderContext.DecodeAddressOrNull();
                txReceipt.FrameReceipts = FrameReceiptRlp.DecodeStoredFrames(ref decoderContext, CompactLogEntryDecoder.Instance);
            }

            // Handle any remaining extra bytes
            if (decoderContext.Position < receiptEnd && allowExtraBytes)
            {
                decoderContext.Position = receiptEnd;
            }

            // EIP-7668: the bloom would be discarded. Otherwise TxReceipt computes it from the logs on first read.
            if ((rlpBehaviors & RlpBehaviors.Eip7668Receipts) != 0)
            {
                txReceipt.Bloom = Bloom.ZeroLength;
            }

            return txReceipt;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnexpectedPostStateLength(int length)
                => throw new RlpException($"Unexpected post-transaction state length {length}; expected 0 or {Hash256.Size} bytes");
        }

        public void DecodeStructRef(scoped ref RlpReader decoderContext, RlpBehaviors rlpBehaviors,
            out TxReceiptStructRef item)
        {
            // Note: This method runs at 2.5 million times/sec on my machine
            item = new TxReceiptStructRef();

            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return;
            }

            (int prefixLength, int contentLength) = decoderContext.PeekPrefixAndContentLength();
            int receiptEnd = decoderContext.Position + prefixLength + contentLength;
            decoderContext.SkipLength();

            ReadOnlySpan<byte> firstItem = decoderContext.DecodeByteArraySpan(RlpLimit.L32);
            if (firstItem.Length == 1)
            {
                item.StatusCode = firstItem[0];
            }
            else
            {
                item.PostTransactionState =
                    firstItem.Length == 0 ? new Hash256StructRef() : new Hash256StructRef(firstItem);
            }

            decoderContext.DecodeAddressStructRef(out item.Sender);
            item.GasUsedTotal = decoderContext.DecodeULong();

            (int PrefixLength, int ContentLength) =
                decoderContext.PeekPrefixAndContentLength();
            int logsBytes = ContentLength + PrefixLength;
            item.LogsRlp = decoderContext.Data.Slice(decoderContext.Position, logsBytes);
            decoderContext.SkipItem();

            // EIP-8141: skip a frame-tx receipt's trailing extension so the next receipt in the array stays aligned.
            if (decoderContext.Position < receiptEnd)
            {
                item.TxType = TxType.FrameTx;
                decoderContext.Position = receiptEnd;
            }
        }

        public void DecodeLogEntryStructRef(scoped ref RlpReader decoderContext, RlpBehaviors none,
            out LogEntryStructRef current) => CompactLogEntryDecoder.DecodeLogEntryStructRef(ref decoderContext, none, out current);

        public Hash256[] DecodeTopics(RlpReader reader) => CompactLogEntryDecoder.DecodeTopics(reader);

        // Refstruct decode does not generate bloom
        public bool CanDecodeBloom => false;

        public override void Encode<TWriter>(TxReceipt? item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            lengths.Pop();
            if (item is null)
            {
                writer.WriteByte(Rlp.EmptyListByte);
                return;
            }

            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            // Note: Any byte saved here is about 3GB on mainnet.
            writer.StartSequence(lengths.Pop());
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

            CompactLogEntryDecoder.Instance.EncodeSequence(GetLogs(item), ref lengths, ref writer);

            if (item.TxType == TxType.FrameTx)
            {
                // Repeats the logs the top-level union already holds, at the cost noted above: DecodeStructRef
                // hands eth_getLogs one contiguous LogsRlp span, which N per-frame sequences cannot supply.
                FrameReceiptRlp.EncodeStoredExtension(ref writer, item, CompactLogEntryDecoder.Instance, ref lengths);
            }
        }

        public override void PopulateLength(TxReceipt? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                lengths.Push(Rlp.OfEmptyList.Length);
                return;
            }

            int contentLength = 0;
            if (item.TxType == TxType.FrameTx)
            {
                contentLength += FrameReceiptRlp.PopulateStoredExtensionLength(item, CompactLogEntryDecoder.Instance, ref lengths);
            }

            contentLength += CompactLogEntryDecoder.Instance.PopulateSequenceAndPeek(GetLogs(item), ref lengths);

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

            lengths.Push(contentLength);
            lengths.Push(Rlp.LengthOfSequence(contentLength));
        }

        private static LogEntry[] GetLogs(TxReceipt item)
            => item.Logs ?? throw new RlpException("Receipt logs are null.");
    }
}
