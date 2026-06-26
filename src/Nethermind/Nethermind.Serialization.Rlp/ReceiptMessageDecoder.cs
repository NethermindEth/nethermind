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

        [return: MaybeNull]
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

            int numberOfReceipts = ctx.PeekNumberOfItemsRemaining(lastCheck);
            ctx.GuardLimit(numberOfReceipts, LogsRlpLimit);
            LogEntry[] entries = new LogEntry[numberOfReceipts];
            for (int i = 0; i < numberOfReceipts; i++)
            {
                entries[i] = LogEntryDecoder.Instance.Decode(ref ctx, RlpBehaviors.AllowExtraBytes)
                    ?? throw new RlpException("Receipt log decoding returned null.");
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

        private (int Total, int Logs) GetContentLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return (0, 0);
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
            LogEntry[] logs = item.Logs ?? [];
            for (int i = 0; i < logs.Length; i++)
            {
                logsLength += Rlp.LengthOf(logs[i]);
            }

            return logsLength;
        }

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
            LogEntry[] logs = item.Logs ?? [];
            LogEntryDecoder logEntryDecoder = LogEntryDecoder.Instance;
            for (int i = 0; i < logs.Length; i++)
            {
                logEntryDecoder.Encode(ref writer, logs[i]);
            }
        }
    }
}
