// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using System;
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
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            TxReceipt txReceipt = new();
            int receiptEnd = decoderContext.ReadSequenceLength() + decoderContext.Position;

            byte[] firstItem = decoderContext.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
            }

            txReceipt.Sender = decoderContext.DecodeAddress();
            txReceipt.GasUsedTotal = decoderContext.DecodePositiveLong();

            int sequenceLength = decoderContext.ReadSequenceLength();
            int lastCheck = sequenceLength + decoderContext.Position;

            // Don't know the size exactly, I'll just assume its just an address and add some margin
            using ArrayPoolListRef<LogEntry> logEntries = new(sequenceLength * 2 / Rlp.LengthOfAddressRlp);
            while (decoderContext.Position < lastCheck)
            {
                logEntries.Add(CompactLogEntryDecoder.Instance.Decode(ref decoderContext, RlpBehaviors.AllowExtraBytes));
            }

            txReceipt.Logs = logEntries.ToArray();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                decoderContext.Check(lastCheck);
            }

            // Handle any remaining extra bytes
            if (decoderContext.Position < receiptEnd && allowExtraBytes)
            {
                decoderContext.Position = receiptEnd;
            }

            txReceipt.Bloom = new Bloom(txReceipt.Logs);

            return txReceipt;
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
            item.GasUsedTotal = decoderContext.DecodePositiveLong();

            (int PrefixLength, int ContentLength) =
                decoderContext.PeekPrefixAndContentLength();
            int logsBytes = ContentLength + PrefixLength;
            item.LogsRlp = decoderContext.Data.Slice(decoderContext.Position, logsBytes);
            decoderContext.SkipItem();

            // Handle any remaining extra bytes
            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (decoderContext.Position < receiptEnd && allowExtraBytes)
            {
                decoderContext.Position = receiptEnd;
            }
        }

        public void DecodeLogEntryStructRef(scoped ref RlpReader decoderContext, RlpBehaviors none,
            out LogEntryStructRef current) => CompactLogEntryDecoder.DecodeLogEntryStructRef(ref decoderContext, none, out current);

        public Hash256[] DecodeTopics(RlpReader reader) => CompactLogEntryDecoder.DecodeTopics(reader);

        // Refstruct decode does not generate bloom
        public bool CanDecodeBloom => false;

        public override void Encode<TWriter>(ref TWriter writer, TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.WriteByte(Rlp.EmptyListByte);
                return;
            }

            (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);

            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            // Note: Any byte saved here is about 3GB on mainnet.
            writer.StartSequence(totalContentLength);
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

            writer.StartSequence(logsLength);

            LogEntry[] logs = item.Logs ?? [];
            for (int i = 0; i < logs.Length; i++)
            {
                CompactLogEntryDecoder.Instance.Encode(ref writer, logs[i]);
            }
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

            return (contentLength, logsLength);
        }

        private static int GetLogsLength(TxReceipt item)
        {
            int logsLength = 0;
            LogEntry[] logs = item.Logs ?? [];
            for (int i = 0; i < logs.Length; i++)
            {
                logsLength += CompactLogEntryDecoder.Instance.GetLength(logs[i]);
            }

            return logsLength;
        }

        public override int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            (int Total, _) = GetContentLength(item, rlpBehaviors);
            return Rlp.LengthOfSequence(Total);
        }
    }
}
