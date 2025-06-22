// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp
{
    [Rlp.Decoder(RlpDecoderKey.Default)]
    [Rlp.Decoder(RlpDecoderKey.Trie)]
    public class ReceiptMessageDecoder : IRlpStreamDecoder<TxReceipt>, IRlpValueDecoder<TxReceipt>
    {
        public TxReceipt Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            Span<byte> span = rlpStream.PeekNextItem();
            Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(span);
            TxReceipt response = Decode(ref ctx, rlpBehaviors);
            rlpStream.SkipItem();

            return response;
        }

        public TxReceipt Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (ctx.IsNextItemNull())
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

            _ = ctx.ReadSequenceLength();
            byte[] firstItem = ctx.DecodeByteArray();
            if (firstItem.Length == 1 && (firstItem[0] == 0 || firstItem[0] == 1))
            {
                txReceipt.StatusCode = firstItem[0];
                txReceipt.GasUsedTotal = (long)ctx.DecodeUBigInt();
            }
            else if (firstItem.Length is >= 1 and <= 4)
            {
                txReceipt.GasUsedTotal = (long)firstItem.ToUnsignedBigInteger();
                txReceipt.SkipStateAndStatusInRlp = true;
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
                txReceipt.GasUsedTotal = (long)ctx.DecodeUBigInt();
            }

            txReceipt.Bloom = ctx.DecodeBloom();

            int lastCheck = ctx.ReadSequenceLength() + ctx.Position;

            int numberOfReceipts = ctx.PeekNumberOfItemsRemaining(lastCheck);
            LogEntry[] entries = new LogEntry[numberOfReceipts];
            for (int i = 0; i < numberOfReceipts; i++)
            {
                entries[i] = Rlp.Decode<LogEntry>(ref ctx, RlpBehaviors.AllowExtraBytes);
            }
            txReceipt.Logs = entries;

            return txReceipt;
        }

        private static (int Total, int Logs) GetContentLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return (0, 0);
            }

            int contentLength = 0;
            contentLength += Rlp.LengthOf(item.GasUsedTotal);
            contentLength += Rlp.LengthOf(item.Bloom);

            int logsLength = GetLogsLength(item);
            contentLength += Rlp.LengthOfSequence(logsLength);

            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (!item.SkipStateAndStatusInRlp && (rlpBehaviors & RlpBehaviors.SkipReceiptStateAndStatus) == RlpBehaviors.None)
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
            for (var i = 0; i < item.Logs.Length; i++)
            {
                logsLength += Rlp.LengthOf(item.Logs[i]);
            }

            return logsLength;
        }

        /// <summary>
        /// https://eips.ethereum.org/EIPS/eip-2718
        /// </summary>
        public int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
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
                return Rlp.OfEmptySequence.Bytes;
            }

            int length = GetLength(item, rlpBehaviors);
            RlpStream stream = new(length);
            Encode(stream, item, rlpBehaviors);
            return stream.Data.ToArray();
        }

        public void Encode(RlpStream rlpStream, TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);
            int sequenceLength = Rlp.LengthOfSequence(totalContentLength);

            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (item.TxType != TxType.Legacy)
            {
                if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.None)
                {
                    rlpStream.StartByteArray(sequenceLength + 1, false);
                }

                rlpStream.WriteByte((byte)item.TxType);
            }

            rlpStream.StartSequence(totalContentLength);
            if (!item.SkipStateAndStatusInRlp && (rlpBehaviors & RlpBehaviors.SkipReceiptStateAndStatus) == RlpBehaviors.None)
            {
                if (isEip658Receipts)
                {
                    rlpStream.Encode(item.StatusCode);
                }
                else
                {
                    rlpStream.Encode(item.PostTransactionState);
                }
            }

            rlpStream.Encode(item.GasUsedTotal);
            rlpStream.Encode(item.Bloom);

            rlpStream.StartSequence(logsLength);
            LogEntry[] logs = item.Logs;
            for (var i = 0; i < logs.Length; i++)
            {
                rlpStream.Encode(logs[i]);
            }
        }
    }
}
