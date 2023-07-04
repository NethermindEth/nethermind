// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp
{
    public class ReceiptMessageDecoder : IRlpStreamDecoder<TxReceipt>
    {
        static ReceiptMessageDecoder()
        {
            Rlp.Decoders[typeof(TxReceipt)] = new ReceiptMessageDecoder();
        }

        public TxReceipt Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            TxReceipt txReceipt = new();
            if (!rlpStream.IsSequenceNext())
            {
                rlpStream.SkipLength();
                txReceipt.TxType = (TxType)rlpStream.ReadByte();
            }

            _ = rlpStream.ReadSequenceLength();
            byte[] firstItem = rlpStream.DecodeByteArray();
            if (firstItem.Length == 1 && (firstItem[0] == 0 || firstItem[0] == 1))
            {
                txReceipt.StatusCode = firstItem[0];
                txReceipt.GasUsedTotal = (long)rlpStream.DecodeUBigInt();
            }
            else if (firstItem.Length >= 1 && firstItem.Length <= 4)
            {
                txReceipt.GasUsedTotal = (long)firstItem.ToUnsignedBigInteger();
                txReceipt.SkipStateAndStatusInRlp = true;
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
                txReceipt.GasUsedTotal = (long)rlpStream.DecodeUBigInt();
            }

            txReceipt.Bloom = rlpStream.DecodeBloom();

            int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;

            int numberOfReceipts = rlpStream.PeekNumberOfItemsRemaining(lastCheck);
            LogEntry[] entries = new LogEntry[numberOfReceipts];
            for (int i = 0; i < numberOfReceipts; i++)
            {
                entries[i] = Rlp.Decode<LogEntry>(rlpStream, RlpBehaviors.AllowExtraBytes);
            }

            txReceipt.Logs = entries;
            return txReceipt;
        }

        private (int Total, int Logs) GetContentLength(TxReceipt item, RlpBehaviors rlpBehaviors)
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

            if (!item.SkipStateAndStatusInRlp)
            {
                contentLength += isEip658Receipts
                    ? Rlp.LengthOf(item.StatusCode)
                    : Rlp.LengthOf(item.PostTransactionState);
            }

            return (contentLength, logsLength);
        }

        private int GetLogsLength(TxReceipt item)
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
            (int Total, int Logs) length = GetContentLength(item, rlpBehaviors);
            int receiptPayloadLength = Rlp.LengthOfSequence(length.Total);

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
            return stream.Data;
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
            if (!item.SkipStateAndStatusInRlp)
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
            for (var i = 0; i < item.Logs.Length; i++)
            {
                rlpStream.Encode(item.Logs[i]);
            }
        }
    }
}
