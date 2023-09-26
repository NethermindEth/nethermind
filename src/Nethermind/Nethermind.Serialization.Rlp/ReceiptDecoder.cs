// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

#pragma warning disable 618

namespace Nethermind.Serialization.Rlp
{
    public class ReceiptDecoder : IRlpStreamDecoder<TxReceipt>, IRlpValueDecoder<TxReceipt>, IRlpObjectDecoder<TxReceipt>, IReceiptRefDecoder
    {
        private readonly bool _supportTxHash;
        private const byte MarkTxHashByte = 255;

        public static readonly ReceiptStorageDecoder Instance = new();

        public ReceiptDecoder(bool supportTxHash = true)
        {
            _supportTxHash = supportTxHash;
        }

        public TxReceipt? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

            rlpStream.ReadSequenceLength();
            byte[] firstItem = rlpStream.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
            }

            txReceipt.GasUsedTotal = (long)rlpStream.DecodeUBigInt();
            txReceipt.Bloom = rlpStream.DecodeBloom();

            int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;
            List<LogEntry> logEntries = new();

            while (rlpStream.Position < lastCheck)
            {
                logEntries.Add(Rlp.Decode<LogEntry>(rlpStream, RlpBehaviors.AllowExtraBytes));
            }

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                rlpStream.Check(lastCheck);
            }

            if (!allowExtraBytes)
            {
                // since error was added later we can only rely on it in cases where we read receipt only and no data follows, empty errors might not be serialized
                if (rlpStream.Position != rlpStream.Length)
                {
                    txReceipt.Error = rlpStream.DecodeString();
                }
            }

            txReceipt.Logs = logEntries.ToArray();
            return txReceipt;
        }

        public TxReceipt? Decode(ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            TxReceipt txReceipt = new();
            if (!decoderContext.IsSequenceNext())
            {
                decoderContext.SkipLength();
                txReceipt.TxType = (TxType)decoderContext.ReadByte();
            }

            decoderContext.ReadSequenceLength();
            byte[] firstItem = decoderContext.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
            }

            txReceipt.GasUsedTotal = (long)decoderContext.DecodeUBigInt();
            txReceipt.Bloom = decoderContext.DecodeBloom();

            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            List<LogEntry> logEntries = new();

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

                // since error was added later we can only rely on it in cases where we read receipt only and no data follows, empty errors might not be serialized
                if (decoderContext.Position != decoderContext.Length)
                {
                    txReceipt.Error = decoderContext.DecodeString();
                }
            }

            txReceipt.Logs = logEntries.ToArray();
            return txReceipt;
        }

        public Rlp Encode(TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(RlpStream rlpStream, TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);
            int sequenceLength = Rlp.LengthOfSequence(totalContentLength);

            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (item.TxType != TxType.Legacy)
            {
                if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.None)
                {
                    rlpStream.StartByteArray(sequenceLength + 1, false);
                }

                rlpStream.WriteByte((byte)item.TxType);
            }

            rlpStream.StartSequence(totalContentLength);
            if (isEip658receipts)
            {
                rlpStream.Encode(item.StatusCode);
            }
            else
            {
                rlpStream.Encode(item.PostTransactionState);
            }

   
            rlpStream.Encode(item.GasUsedTotal);
            rlpStream.Encode(item.Bloom);

            rlpStream.StartSequence(logsLength);

            for (int i = 0; i < item.Logs.Length; i++)
            {
                rlpStream.Encode(item.Logs[i]);
            }

            rlpStream.Encode(item.Error);
        }

        private (int Total, int Logs) GetContentLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
        {
            int contentLength = 0;
            if (item is null)
            {
                return (contentLength, 0);
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

            return (contentLength, logsLength);
        }

        private int GetLogsLength(TxReceipt item)
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
        public int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            (int Total, int Logs) length = GetContentLength(item, rlpBehaviors);
            int receiptPayloadLength = Rlp.LengthOfSequence(length.Total);

            bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
            int result = item.TxType != TxType.Legacy
                ? isForTxRoot
                    ? (1 + receiptPayloadLength)
                    : Rlp.LengthOfSequence(1 + receiptPayloadLength) // Rlp(TransactionType || ReceiptPayload)
                : receiptPayloadLength;
            return result;
        }

        public void DecodeStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors,
            out TxReceiptStructRef item)
        {
            item = new TxReceiptStructRef();

            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return;
            }

            if (!decoderContext.IsSequenceNext())
            {
                decoderContext.SkipLength();
                item.TxType = (TxType)decoderContext.ReadByte();
            }

            decoderContext.ReadSequenceLength();
            Span<byte> firstItem = decoderContext.DecodeByteArraySpan();
            if (firstItem.Length == 1)
            {
                item.StatusCode = firstItem[0];
            }
            else
            {
                item.PostTransactionState =
                    firstItem.Length == 0 ? new KeccakStructRef() : new KeccakStructRef(firstItem);
            }

            item.GasUsedTotal = (long)decoderContext.DecodeUBigInt();
            decoderContext.DecodeBloomStructRef(out item.Bloom);

            (int PrefixLength, int ContentLength) peekPrefixAndContentLength =
                decoderContext.PeekPrefixAndContentLength();
            int logsBytes = peekPrefixAndContentLength.ContentLength + peekPrefixAndContentLength.PrefixLength;
            item.LogsRlp = decoderContext.Data.Slice(decoderContext.Position, logsBytes);
            decoderContext.SkipItem();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {

                // since error was added later we can only rely on it in cases where we read receipt only and no data follows, empty errors might not be serialized
                if (decoderContext.Position != decoderContext.Length)
                {
                    item.Error = decoderContext.DecodeString();
                }
            }
        }

        public void DecodeLogEntryStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors behaviour,
            out LogEntryStructRef current)
        {
            LogEntryDecoder.DecodeStructRef(ref decoderContext, behaviour, out current);
        }

        public Keccak[] DecodeTopics(Rlp.ValueDecoderContext valueDecoderContext)
        {
            return KeccakDecoder.Instance.DecodeArray(ref valueDecoderContext);
        }

        public bool CanDecodeBloom => true;
    }
}
