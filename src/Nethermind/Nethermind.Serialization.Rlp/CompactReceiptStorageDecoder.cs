// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

#pragma warning disable 618

namespace Nethermind.Serialization.Rlp
{
    [Rlp.SkipGlobalRegistration]
    public class CompactReceiptStorageDecoder : IRlpStreamDecoder<TxReceipt>, IRlpValueDecoder<TxReceipt>, IRlpObjectDecoder<TxReceipt>, IReceiptRefDecoder
    {
        public static readonly CompactReceiptStorageDecoder Instance = new();

        public CompactReceiptStorageDecoder()
        {
        }

        public TxReceipt? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            TxReceipt txReceipt = new();
            rlpStream.ReadSequenceLength();

            byte[] firstItem = rlpStream.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
            }

            txReceipt.Sender = rlpStream.DecodeAddress();
            txReceipt.GasUsedTotal = (long)rlpStream.DecodeUBigInt();

            int sequenceLength = rlpStream.ReadSequenceLength();
            int lastCheck = sequenceLength + rlpStream.Position;
            using ArrayPoolList<LogEntry> logEntries = new(sequenceLength * 2 / Rlp.LengthOfAddressRlp);

            while (rlpStream.Position < lastCheck)
            {
                logEntries.Add(CompactLogEntryDecoder.Instance.Decode(rlpStream, RlpBehaviors.AllowExtraBytes));
            }

            txReceipt.Logs = logEntries.ToArray();

            bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
            if (!allowExtraBytes)
            {
                rlpStream.Check(lastCheck);
            }

            txReceipt.Bloom = new Bloom(txReceipt.Logs);

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
            decoderContext.ReadSequenceLength();

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
            txReceipt.GasUsedTotal = (long)decoderContext.DecodeUBigInt();

            int sequenceLength = decoderContext.ReadSequenceLength();
            int lastCheck = sequenceLength + decoderContext.Position;

            // Don't know the size exactly, I'll just assume its just an address and add some margin
            using ArrayPoolList<LogEntry> logEntries = new(sequenceLength * 2 / Rlp.LengthOfAddressRlp);
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

            txReceipt.Bloom = new Bloom(txReceipt.Logs);

            return txReceipt;
        }

        public void DecodeStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors,
            out TxReceiptStructRef item)
        {
            // Note: This method runs at 2.5 million times/sec on my machine
            item = new TxReceiptStructRef();

            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return;
            }

            decoderContext.SkipLength();

            Span<byte> firstItem = decoderContext.DecodeByteArraySpan();
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
            item.GasUsedTotal = (long)decoderContext.DecodeUBigInt();

            (int PrefixLength, int ContentLength) peekPrefixAndContentLength =
                decoderContext.PeekPrefixAndContentLength();
            int logsBytes = peekPrefixAndContentLength.ContentLength + peekPrefixAndContentLength.PrefixLength;
            item.LogsRlp = decoderContext.Data.Slice(decoderContext.Position, logsBytes);
            decoderContext.SkipItem();
        }

        public void DecodeLogEntryStructRef(scoped ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors none,
            out LogEntryStructRef current)
        {
            CompactLogEntryDecoder.Instance.DecodeLogEntryStructRef(ref decoderContext, none, out current);
        }

        public Hash256[] DecodeTopics(Rlp.ValueDecoderContext valueDecoderContext)
        {
            return CompactLogEntryDecoder.Instance.DecodeTopics(valueDecoderContext);
        }

        // Refstruct decode does not generate bloom
        public bool CanDecodeBloom => false;

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

            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            // Note: Any byte saved here is about 3GB on mainnet.
            rlpStream.StartSequence(totalContentLength);
            if (isEip658receipts)
            {
                rlpStream.Encode(item.StatusCode);
            }
            else
            {
                rlpStream.Encode(item.PostTransactionState);
            }

            rlpStream.Encode(item.Sender);
            rlpStream.Encode(item.GasUsedTotal);

            rlpStream.StartSequence(logsLength);

            LogEntry[] logs = item.Logs ?? Array.Empty<LogEntry>();
            for (int i = 0; i < logs.Length; i++)
            {
                CompactLogEntryDecoder.Instance.Encode(rlpStream, logs[i]);
            }
        }

        private (int Total, int Logs) GetContentLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
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

        private int GetLogsLength(TxReceipt item)
        {
            int logsLength = 0;
            LogEntry[] logs = item.Logs ?? Array.Empty<LogEntry>();
            for (int i = 0; i < logs.Length; i++)
            {
                logsLength += CompactLogEntryDecoder.Instance.GetLength(logs[i]);
            }

            return logsLength;
        }

        public int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            (int Total, int Logs) length = GetContentLength(item, rlpBehaviors);
            return Rlp.LengthOfSequence(length.Total);
        }
    }
}
