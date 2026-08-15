// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

#pragma warning disable 618

namespace Nethermind.Serialization.Rlp
{
    // EIP-8141: frame receipts append [payer, [frame_receipt, ...]] after the standard storage
    // fields (after Error here, after the logs sequence in CompactReceiptStorageDecoder). Only
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

        protected override TxReceipt? DecodeInternal(ref RlpReader decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;
            TxReceipt txReceipt = new();
            if (!decoderContext.IsSequenceNext())
            {
                decoderContext.SkipLength();
                txReceipt.TxType = (TxType)decoderContext.ReadByte();
            }

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

            if (isStorage) txReceipt.BlockHash = decoderContext.DecodeKeccak();
            if (isStorage) txReceipt.BlockNumber = decoderContext.DecodeULong();
            if (isStorage) txReceipt.Index = decoderContext.DecodePositiveInt();
            if (isStorage) txReceipt.Sender = decoderContext.DecodeAddress();
            if (isStorage) txReceipt.Recipient = decoderContext.DecodeAddress();
            if (isStorage) txReceipt.ContractAddress = decoderContext.DecodeAddress();
            if (isStorage) txReceipt.GasUsed = decoderContext.DecodeULong();
            txReceipt.GasUsedTotal = decoderContext.DecodeULong();
            txReceipt.Bloom = decoderContext.DecodeBloom();

            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            List<LogEntry> logEntries = [];

            while (decoderContext.Position < lastCheck)
            {
                logEntries.Add(Rlp.Decode<LogEntry>(ref decoderContext, RlpBehaviors.AllowExtraBytes));
            }

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                decoderContext.Check(lastCheck);
            }

            if (!allowExtraBytes)
            {
                if (isStorage && supportTxHash && decoderContext.Position < receiptEnd)
                {
                    // since txHash was added later and may not be in rlp, we provide special mark byte that it will be next
                    if (decoderContext.PeekByte() == MarkTxHashByte)
                    {
                        decoderContext.ReadByte();
                        txReceipt.TxHash = decoderContext.DecodeKeccak();
                    }
                }

                // since error was added later we can only rely on it in cases where we read receipt only and no data follows, empty errors might not be serialized
                if (decoderContext.Position < receiptEnd)
                {
                    txReceipt.Error = decoderContext.DecodeString();
                }

                if (txReceipt.TxType == TxType.FrameTx && decoderContext.Position < receiptEnd)
                {
                    txReceipt.Payer = decoderContext.DecodeAddress();
                    txReceipt.FrameReceipts = DecodeFrameReceipts(ref decoderContext);
                }
            }

            txReceipt.Logs = logEntries.ToArray();
            return txReceipt;
        }

        public override void Encode<TWriter>(ref TWriter writer, TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.WriteByte(Rlp.EmptyListByte);
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

            writer.StartSequence(totalContentLength);
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

                writer.StartSequence(logsLength);

                LogEntry[] logs = item.Logs;
                for (int i = 0; i < logs.Length; i++)
                {
                    LogEntryDecoder.Instance.Encode(ref writer, logs[i]);
                }

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

                writer.StartSequence(logsLength);

                LogEntry[] logs = item.Logs;
                for (int i = 0; i < logs.Length; i++)
                {
                    LogEntryDecoder.Instance.Encode(ref writer, logs[i]);
                }

                writer.Encode(item.Error);
            }

            if (item.TxType == TxType.FrameTx)
            {
                writer.Encode(item.Payer);
                EncodeFrameReceipts(ref writer, item.FrameReceipts ?? []);
            }
        }

        private static TxFrameReceipt[] DecodeFrameReceipts(ref RlpReader decoderContext)
        {
            int framesEnd = decoderContext.ReadSequenceLength() + decoderContext.Position;
            List<TxFrameReceipt> frameReceipts = [];
            while (decoderContext.Position < framesEnd)
            {
                int frameEnd = decoderContext.ReadSequenceLength() + decoderContext.Position;
                byte status = decoderContext.DecodeByte();
                ulong gasUsed = decoderContext.DecodeULong();

                int logsEnd = decoderContext.ReadSequenceLength() + decoderContext.Position;
                List<LogEntry> frameLogs = [];
                while (decoderContext.Position < logsEnd)
                {
                    frameLogs.Add(Rlp.Decode<LogEntry>(ref decoderContext, RlpBehaviors.AllowExtraBytes));
                }

                frameReceipts.Add(new TxFrameReceipt(status, gasUsed, frameLogs.ToArray()));
                decoderContext.Check(frameEnd);
            }

            return frameReceipts.ToArray();
        }

        private static void EncodeFrameReceipts<TWriter>(ref TWriter writer, TxFrameReceipt[] frameReceipts)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            int framesLength = 0;
            for (int i = 0; i < frameReceipts.Length; i++)
            {
                framesLength += Rlp.LengthOfSequence(GetFrameReceiptContentLength(frameReceipts[i]));
            }

            writer.StartSequence(framesLength);
            for (int i = 0; i < frameReceipts.Length; i++)
            {
                TxFrameReceipt frameReceipt = frameReceipts[i];
                int logsLength = GetFrameLogsLength(frameReceipt);
                writer.StartSequence(Rlp.LengthOf((ulong)frameReceipt.Status) + Rlp.LengthOf(frameReceipt.GasUsed) + Rlp.LengthOfSequence(logsLength));
                writer.Encode((ulong)frameReceipt.Status);
                writer.Encode(frameReceipt.GasUsed);
                writer.StartSequence(logsLength);
                for (int j = 0; j < frameReceipt.Logs.Length; j++)
                {
                    LogEntryDecoder.Instance.Encode(ref writer, frameReceipt.Logs[j]);
                }
            }
        }

        private static int GetFrameReceiptContentLength(TxFrameReceipt frameReceipt) =>
            Rlp.LengthOf((ulong)frameReceipt.Status)
            + Rlp.LengthOf(frameReceipt.GasUsed)
            + Rlp.LengthOfSequence(GetFrameLogsLength(frameReceipt));

        private static int GetFrameLogsLength(TxFrameReceipt frameReceipt)
        {
            int logsLength = 0;
            for (int i = 0; i < frameReceipt.Logs.Length; i++)
            {
                logsLength += Rlp.LengthOf(frameReceipt.Logs[i]);
            }

            return logsLength;
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
                contentLength += Rlp.LengthOf(item.Payer);
                TxFrameReceipt[] frameReceipts = item.FrameReceipts ?? [];
                int framesLength = 0;
                for (int i = 0; i < frameReceipts.Length; i++)
                {
                    framesLength += Rlp.LengthOfSequence(GetFrameReceiptContentLength(frameReceipts[i]));
                }

                contentLength += Rlp.LengthOfSequence(framesLength);
            }

            return (contentLength, logsLength);
        }

        private static int GetLogsLength(TxReceipt item)
        {
            int logsLength = 0;
            for (int i = 0; i < item.Logs.Length; i++)
            {
                logsLength += Rlp.LengthOf(item.Logs[i]);
            }

            return logsLength;
        }

        /// <summary>
        /// https://eips.ethereum.org/EIPS/eip-2718
        /// </summary>
        public override int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
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
            item = new TxReceiptStructRef();

            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return;
            }

            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;
            if (!decoderContext.IsSequenceNext())
            {
                decoderContext.SkipLength();
                item.TxType = (TxType)decoderContext.ReadByte();
            }

            (int prefixLength, int contentLength) = decoderContext.PeekPrefixAndContentLength();
            int receiptEnd = decoderContext.Position + prefixLength + contentLength;
            decoderContext.ReadSequenceLength();
            ReadOnlySpan<byte> firstItem = decoderContext.DecodeByteArraySpan();
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
                decoderContext.DecodeKeccakStructRef(out item.BlockHash);
                item.BlockNumber = decoderContext.DecodeULong();
                item.Index = decoderContext.DecodePositiveInt();
                decoderContext.DecodeAddressStructRef(out item.Sender);
                decoderContext.DecodeAddressStructRef(out item.Recipient);
                decoderContext.DecodeAddressStructRef(out item.ContractAddress);
                item.GasUsed = decoderContext.DecodeULong();
            }
            item.GasUsedTotal = decoderContext.DecodeULong();
            decoderContext.DecodeBloomStructRef(out item.Bloom);

            (int PrefixLength, int ContentLength) =
                decoderContext.PeekPrefixAndContentLength();
            int logsBytes = ContentLength + PrefixLength;
            item.LogsRlp = decoderContext.Data.Slice(decoderContext.Position, logsBytes);
            decoderContext.SkipItem();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                if (isStorage && supportTxHash && decoderContext.Position < receiptEnd)
                {
                    // since txHash was added later and may not be in rlp, we provide special mark byte that it will be next
                    if (decoderContext.PeekByte() == MarkTxHashByte)
                    {
                        decoderContext.ReadByte();
                        decoderContext.DecodeKeccakStructRef(out item.TxHash);
                    }
                }

                // since error was added later we can only rely on it in cases where we read receipt only and no data follows, empty errors might not be serialized
                if (decoderContext.Position < receiptEnd)
                {
                    item.Error = decoderContext.DecodeString();
                }

                // EIP-8141: skip the frame extension — ref-struct consumers only iterate logs.
                if (decoderContext.Position < receiptEnd)
                {
                    decoderContext.Position = receiptEnd;
                }
            }
        }

        public void DecodeLogEntryStructRef(scoped ref RlpReader decoderContext, RlpBehaviors behaviour,
            out LogEntryStructRef current) => LogEntryDecoder.DecodeStructRef(ref decoderContext, behaviour, out current);

        public Hash256[] DecodeTopics(RlpReader reader) => HashDecoder.DecodeArray(ref reader);

        public bool CanDecodeBloom => true;
    }
}
