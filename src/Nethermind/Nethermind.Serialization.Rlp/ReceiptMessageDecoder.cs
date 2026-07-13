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
        // A 100M gas ceiling still allows roughly 266k LOG0 emissions after intrinsic gas.
        private static readonly RlpLimit LogsRlpLimit = RlpLimit.For<TxReceipt>(270_000, nameof(TxReceipt.Logs));

        protected override TxReceipt DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (ctx.IsNextItemEmptyList())
            {
                ctx.ReadByte();
                return null;
            }

            TxReceipt txReceipt = new();
            if (!ctx.IsSequenceNext())
            {
                ctx.SkipLength();
                txReceipt.TxType = (TxType)ctx.ReadByte();
            }

            if (txReceipt.TxType == TxType.FrameTx)
            {
                DecodeFrameTxReceipt(txReceipt, ref ctx, rlpBehaviors);
                return txReceipt;
            }

            int sequenceLength = ctx.ReadSequenceLength();
            int receiptEnd = ctx.Position + sequenceLength;
            byte[] firstItem = ctx.DecodeByteArray();
            if (firstItem.Length == 1 && (firstItem[0] == 0 || firstItem[0] == 1))
            {
                txReceipt.StatusCode = firstItem[0];
                txReceipt.GasUsedTotal = ctx.DecodeULong();
            }
            else if (firstItem.Length is >= 1 and <= 4)
            {
                txReceipt.GasUsedTotal = firstItem.ToULong();
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
                txReceipt.GasUsedTotal = ctx.DecodeULong();
            }

            if (!skipBloom)
                txReceipt.Bloom = ctx.DecodeBloom();
            // When _skipBloom is true (slim receipt), bloom is absent from the stream — nothing to skip.

            int lastCheck = ctx.ReadSequenceLength() + ctx.Position;

            int numberOfReceipts = ctx.PeekNumberOfItemsRemaining(lastCheck, LogsRlpLimit.Limit + 1);
            ctx.GuardLimit(numberOfReceipts, LogsRlpLimit);
            LogEntry[] entries = new LogEntry[numberOfReceipts];
            for (int i = 0; i < numberOfReceipts; i++)
            {
                entries[i] = Rlp.Decode<LogEntry>(ref ctx, RlpBehaviors.AllowExtraBytes);
            }
            txReceipt.Logs = entries;

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

        // EIP-8141 ReceiptPayload: [cumulative_gas_used, payer, [frame_receipt, ...]],
        // frame_receipt = [status, gas_used, logs]. Spec-literal — no top-level status and no bloom
        // on the wire (receipts-root parity with other clients).
        // EIP8141-GAP: the spec receipt has no top-level status or bloom; internally StatusCode is
        // set to success for included transactions and Logs holds the union of frame logs so bloom
        // calculation and log indexing keep working.
        private void DecodeFrameTxReceipt(TxReceipt txReceipt, ref RlpReader ctx, RlpBehaviors rlpBehaviors)
        {
            int sequenceLength = ctx.ReadSequenceLength();
            int receiptEnd = ctx.Position + sequenceLength;

            txReceipt.GasUsedTotal = ctx.DecodeULong();
            txReceipt.Payer = ctx.DecodeAddress();

            int framesEnd = ctx.ReadSequenceLength() + ctx.Position;
            int frameCount = ctx.PeekNumberOfItemsRemaining(framesEnd, Eip8141Constants.MaxFrames + 1);
            TxFrameReceipt[] frameReceipts = new TxFrameReceipt[frameCount];
            int totalLogs = 0;
            for (int i = 0; i < frameCount; i++)
            {
                int frameEnd = ctx.ReadSequenceLength() + ctx.Position;
                byte status = ctx.DecodeByte();
                ulong gasUsed = ctx.DecodeULong();

                int logsEnd = ctx.ReadSequenceLength() + ctx.Position;
                int logCount = ctx.PeekNumberOfItemsRemaining(logsEnd, LogsRlpLimit.Limit + 1);
                ctx.GuardLimit(logCount, LogsRlpLimit);
                LogEntry[] logs = new LogEntry[logCount];
                for (int j = 0; j < logCount; j++)
                {
                    logs[j] = Rlp.Decode<LogEntry>(ref ctx, RlpBehaviors.AllowExtraBytes);
                }

                frameReceipts[i] = new TxFrameReceipt(status, gasUsed, logs);
                totalLogs += logCount;
                ctx.Check(frameEnd);
            }

            txReceipt.FrameReceipts = frameReceipts;
            txReceipt.StatusCode = TxFrameReceipt.StatusSuccess;

            LogEntry[] allLogs = new LogEntry[totalLogs];
            int offset = 0;
            for (int i = 0; i < frameReceipts.Length; i++)
            {
                LogEntry[] frameLogs = frameReceipts[i].Logs;
                frameLogs.CopyTo(allLogs, offset);
                offset += frameLogs.Length;
            }

            txReceipt.Logs = allLogs;

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
            {
                ctx.Check(receiptEnd);
            }
            else
            {
                ctx.Position = receiptEnd;
            }
        }

        private static (int Total, int Frames) GetFrameTxContentLength(TxReceipt item)
        {
            int framesLength = 0;
            TxFrameReceipt[] frameReceipts = item.FrameReceipts ?? [];
            for (int i = 0; i < frameReceipts.Length; i++)
            {
                framesLength += Rlp.LengthOfSequence(GetFrameReceiptContentLength(frameReceipts[i]));
            }

            int contentLength = Rlp.LengthOf(item.GasUsedTotal)
                                + Rlp.LengthOf(item.Payer)
                                + Rlp.LengthOfSequence(framesLength);
            return (contentLength, framesLength);
        }

        private static int GetFrameReceiptContentLength(TxFrameReceipt frameReceipt)
        {
            int logsLength = 0;
            for (int i = 0; i < frameReceipt.Logs.Length; i++)
            {
                logsLength += Rlp.LengthOf(frameReceipt.Logs[i]);
            }

            return Rlp.LengthOf((ulong)frameReceipt.Status)
                   + Rlp.LengthOf(frameReceipt.GasUsed)
                   + Rlp.LengthOfSequence(logsLength);
        }

        private static void EncodeFrameTxReceipt<TWriter>(ref TWriter writer, TxReceipt item)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            (int totalContentLength, _) = GetFrameTxContentLength(item);
            writer.StartSequence(totalContentLength);
            writer.Encode(item.GasUsedTotal);
            writer.Encode(item.Payer);

            TxFrameReceipt[] frameReceipts = item.FrameReceipts ?? [];
            int framesLength = 0;
            for (int i = 0; i < frameReceipts.Length; i++)
            {
                framesLength += Rlp.LengthOfSequence(GetFrameReceiptContentLength(frameReceipts[i]));
            }

            writer.StartSequence(framesLength);
            LogEntryDecoder logEntryDecoder = LogEntryDecoder.Instance;
            for (int i = 0; i < frameReceipts.Length; i++)
            {
                TxFrameReceipt frameReceipt = frameReceipts[i];
                int logsLength = 0;
                for (int j = 0; j < frameReceipt.Logs.Length; j++)
                {
                    logsLength += Rlp.LengthOf(frameReceipt.Logs[j]);
                }

                writer.StartSequence(Rlp.LengthOf((ulong)frameReceipt.Status) + Rlp.LengthOf(frameReceipt.GasUsed) + Rlp.LengthOfSequence(logsLength));
                writer.Encode((ulong)frameReceipt.Status);
                writer.Encode(frameReceipt.GasUsed);
                writer.StartSequence(logsLength);
                for (int j = 0; j < frameReceipt.Logs.Length; j++)
                {
                    logEntryDecoder.Encode(ref writer, frameReceipt.Logs[j]);
                }
            }
        }

        private (int Total, int Logs) GetContentLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return (0, 0);
            }

            if (item.TxType == TxType.FrameTx)
            {
                (int frameTxTotal, _) = GetFrameTxContentLength(item);
                return (frameTxTotal, 0);
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

            (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);
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

            if (item.TxType == TxType.FrameTx)
            {
                EncodeFrameTxReceipt(ref writer, item);
                return;
            }

            writer.StartSequence(totalContentLength);
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

            writer.StartSequence(logsLength);
            LogEntry[] logs = item.Logs;
            LogEntryDecoder logEntryDecoder = LogEntryDecoder.Instance;
            for (int i = 0; i < logs.Length; i++)
            {
                logEntryDecoder.Encode(ref writer, logs[i]);
            }
        }
    }
}
